using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Contracts;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;
using PaperTrading.Api.MarketData;

namespace PaperTrading.Api.Services;

public sealed class OrderService(
    AppDbContext db, IMarketDataProvider market, IMarketClock clock, ExecutionGate gate, IOptions<TradingOptions> options)
{
    private const decimal MaxQuantity = 10_000_000m;
    private readonly TradingOptions _options = options.Value;

    public async Task<Order> PlaceAsync(Guid userId, PlaceOrderRequest request, CancellationToken ct = default)
    {
        var symbol = Symbols.Normalize(request.Symbol);
        ValidateShape(request);

        if (request.ClaimId is { } claimId &&
            !await db.Claims.AnyAsync(c => c.Id == claimId && c.Ticker == symbol, ct))
            throw new TradingException($"That prediction isn't about {symbol}.");

        // Network call stays outside the gate so a slow feed can't stall every other account.
        var quote = await market.GetQuoteAsync(symbol, ct)
            ?? throw new TradingException($"Symbol {symbol} was not found.");
        if (!quote.IsTradable)
            throw new TradingException($"{symbol} is not a stock or ETF, so it can't be traded here.");
        if (!quote.Currency.Equals(_options.BaseCurrency, StringComparison.OrdinalIgnoreCase))
            throw new TradingException($"{symbol} trades in {quote.Currency}, but this account trades in {_options.BaseCurrency}.");

        using var _ = await gate.EnterAsync(ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new TradingException("Account not found.", StatusCodes.Status401Unauthorized);

        var order = new Order
        {
            UserId = userId,
            Symbol = symbol,
            Side = request.Side,
            Type = request.Type,
            Quantity = request.Quantity,
            LimitPrice = request.Type == OrderType.Limit ? request.LimitPrice : null,
            StopPrice = request.Type == OrderType.Stop ? request.StopPrice : null,
            ClaimId = request.ClaimId,
        };

        // Checked inside the gate, so a slow request can't slip through just as the market closes.
        var cannotFill = clock.WhyCannotFill(quote);
        if (order.Type == OrderType.Market && cannotFill is not null)
            throw new TradingException($"{cannotFill} A market order can't wait, but a limit or stop order can.", StatusCodes.Status409Conflict);

        ValidateStopDirection(order, quote);
        if (order.Side == OrderSide.Buy)
            await EnsureBuyingPowerAsync(user, order, quote, ct);
        else
            await EnsureSellableAsync(order, ct);

        db.Orders.Add(order);
        if (cannotFill is null && TriggerPrice(order, quote.Price) is { } fillPrice)
            await ExecuteAsync(user, order, fillPrice, ct);

        await db.SaveChangesAsync(ct);
        return order;
    }

    public async Task<Order> CancelAsync(Guid userId, Guid orderId, CancellationToken ct = default)
    {
        using var _ = await gate.EnterAsync(ct);

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct)
            ?? throw new TradingException("Order not found.", StatusCodes.Status404NotFound);
        if (order.Status != OrderStatus.Open)
            throw new TradingException($"This order is already {order.Status.ToString().ToLowerInvariant()}.", StatusCodes.Status409Conflict);

        order.Status = OrderStatus.Cancelled;
        order.ClosedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return order;
    }

    /// <summary>Fills or rejects every resting order whose condition is met at the given prices, if the market allows it.</summary>
    /// <returns>The orders that changed.</returns>
    public async Task<IReadOnlyList<Order>> MatchOpenOrdersAsync(IReadOnlyDictionary<string, Quote> quotes, CancellationToken ct = default)
    {
        using var _ = await gate.EnterAsync(ct);

        var symbols = quotes.Keys.ToList();
        var open = await db.Orders
            .Where(o => o.Status == OrderStatus.Open && symbols.Contains(o.Symbol))
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(ct);

        var changed = new List<Order>();
        foreach (var order in open)
        {
            var quote = quotes[order.Symbol];
            if (clock.WhyCannotFill(quote) is not null) continue;
            if (TriggerPrice(order, quote.Price) is not { } price) continue;

            var user = await db.Users.FirstAsync(u => u.Id == order.UserId, ct);
            await ExecuteAsync(user, order, price, ct);
            await db.SaveChangesAsync(ct);
            changed.Add(order);
        }
        return changed;
    }

    /// <summary>Returns the price the order executes at, or null if its condition isn't met.</summary>
    public static decimal? TriggerPrice(Order order, decimal last) => (order.Type, order.Side) switch
    {
        (OrderType.Market, _) => last,
        (OrderType.Limit, OrderSide.Buy) => last <= order.LimitPrice ? last : null,
        (OrderType.Limit, OrderSide.Sell) => last >= order.LimitPrice ? last : null,
        (OrderType.Stop, OrderSide.Buy) => last >= order.StopPrice ? last : null,
        (OrderType.Stop, OrderSide.Sell) => last <= order.StopPrice ? last : null,
        _ => null,
    };

    /// <summary>Sum of cash held back for open buy orders, valued at their limit or stop price.</summary>
    public decimal ReservedCash(IEnumerable<Order> openBuys) =>
        openBuys.Sum(o => o.Quantity * (o.LimitPrice ?? o.StopPrice ?? 0) + _options.CommissionPerOrder);

    private async Task ExecuteAsync(User user, Order order, decimal price, CancellationToken ct)
    {
        var commission = _options.CommissionPerOrder;
        var position = await db.Positions.FirstOrDefaultAsync(p => p.UserId == user.Id && p.Symbol == order.Symbol, ct);
        decimal? realized = null;

        if (order.Side == OrderSide.Buy)
        {
            var cost = order.Quantity * price + commission;
            if (cost > user.Cash)
            {
                Reject(order, $"Not enough cash when triggered: needed {cost:0.00}, had {user.Cash:0.00}.");
                return;
            }

            user.Cash -= cost;
            if (position is null)
            {
                position = new Position { UserId = user.Id, Symbol = order.Symbol };
                db.Positions.Add(position);
            }
            var newQuantity = position.Quantity + order.Quantity;
            position.AverageCost = Math.Round((position.Quantity * position.AverageCost + cost) / newQuantity, 8);
            position.Quantity = newQuantity;
        }
        else
        {
            if (position is null || position.Quantity < order.Quantity)
            {
                Reject(order, $"Not enough shares when triggered: needed {order.Quantity:0.####}, had {position?.Quantity ?? 0:0.####}.");
                return;
            }

            var proceeds = order.Quantity * price - commission;
            realized = proceeds - order.Quantity * position.AverageCost;
            user.Cash += proceeds;
            position.Quantity -= order.Quantity;
            if (position.Quantity == 0) db.Positions.Remove(position);
        }

        order.Status = OrderStatus.Filled;
        order.FilledPrice = price;
        order.ClosedAt = DateTime.UtcNow;
        db.Fills.Add(new Fill
        {
            OrderId = order.Id,
            UserId = user.Id,
            Symbol = order.Symbol,
            Side = order.Side,
            Quantity = order.Quantity,
            Price = price,
            Commission = commission,
            RealizedPnl = realized,
        });
    }

    private static void Reject(Order order, string reason)
    {
        order.Status = OrderStatus.Rejected;
        order.RejectReason = reason;
        order.ClosedAt = DateTime.UtcNow;
    }

    private static void ValidateShape(PlaceOrderRequest r)
    {
        if (r.Quantity <= 0) throw new TradingException("Quantity must be greater than zero.");
        if (r.Quantity > MaxQuantity) throw new TradingException($"Quantity can't exceed {MaxQuantity:N0}.");
        if (decimal.Round(r.Quantity, 4) != r.Quantity) throw new TradingException("Quantity can have at most 4 decimal places.");

        if (r.Type == OrderType.Limit && r.LimitPrice is not > 0) throw new TradingException("A limit order needs a limit price above zero.");
        if (r.Type == OrderType.Stop && r.StopPrice is not > 0) throw new TradingException("A stop order needs a stop price above zero.");
    }

    private static void ValidateStopDirection(Order order, Quote quote)
    {
        if (order.Type != OrderType.Stop) return;
        if (order.Side == OrderSide.Buy && order.StopPrice <= quote.Price)
            throw new TradingException($"A buy stop must be above the current price ({quote.Price:0.00}).");
        if (order.Side == OrderSide.Sell && order.StopPrice >= quote.Price)
            throw new TradingException($"A sell stop must be below the current price ({quote.Price:0.00}).");
    }

    private async Task EnsureBuyingPowerAsync(User user, Order order, Quote quote, CancellationToken ct)
    {
        var openBuys = await db.Orders
            .Where(o => o.UserId == user.Id && o.Status == OrderStatus.Open && o.Side == OrderSide.Buy)
            .ToListAsync(ct);
        var available = user.Cash - ReservedCash(openBuys);

        var referencePrice = order.LimitPrice ?? order.StopPrice ?? quote.Price;
        // A marketable limit fills at the last price, which is never more than its limit.
        var cost = order.Quantity * referencePrice + _options.CommissionPerOrder;
        if (cost > available)
            throw new TradingException($"Not enough buying power: this order needs {cost:0.00}, you have {available:0.00} available.");
    }

    private async Task EnsureSellableAsync(Order order, CancellationToken ct)
    {
        var position = await db.Positions.FirstOrDefaultAsync(p => p.UserId == order.UserId && p.Symbol == order.Symbol, ct);
        var openSells = await db.Orders
            .Where(o => o.UserId == order.UserId && o.Symbol == order.Symbol && o.Status == OrderStatus.Open && o.Side == OrderSide.Sell)
            .ToListAsync(ct);
        var available = (position?.Quantity ?? 0) - openSells.Sum(o => o.Quantity);

        if (order.Quantity > available)
            throw new TradingException($"You can only sell {available:0.####} {order.Symbol}. Short selling isn't supported, and shares in open sell orders are held back.");
    }
}
