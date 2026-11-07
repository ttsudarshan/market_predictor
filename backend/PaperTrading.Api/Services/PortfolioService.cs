using Microsoft.EntityFrameworkCore;
using PaperTrading.Api.Contracts;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;
using PaperTrading.Api.MarketData;

namespace PaperTrading.Api.Services;

public sealed class PortfolioService(AppDbContext db, IMarketDataProvider market, OrderService orders)
{
    public async Task<PortfolioDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new TradingException("Account not found.", StatusCodes.Status401Unauthorized);
        var positions = await db.Positions.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(ct);
        var openBuys = await db.Orders.AsNoTracking()
            .Where(o => o.UserId == userId && o.Status == OrderStatus.Open && o.Side == OrderSide.Buy)
            .ToListAsync(ct);
        var fills = await db.Fills.AsNoTracking().Where(f => f.UserId == userId).ToListAsync(ct);
        var quotes = await market.GetQuotesAsync(positions.Select(p => p.Symbol), ct);

        var rows = positions.Select(p => ToRow(p, quotes.GetValueOrDefault(p.Symbol), fills))
            .OrderByDescending(r => r.MarketValue)
            .ToList();

        var positionsValue = rows.Sum(r => r.MarketValue);
        var equity = user.Cash + positionsValue;
        var totalReturn = equity - user.StartingBalance;
        var reserved = orders.ReservedCash(openBuys);
        var closed = fills.Where(f => f.RealizedPnl is not null).ToList();

        return new PortfolioDto(
            user.Cash,
            reserved,
            Math.Max(user.Cash - reserved, 0),
            positionsValue,
            equity,
            user.StartingBalance,
            totalReturn,
            user.StartingBalance == 0 ? 0 : Math.Round(totalReturn / user.StartingBalance * 100, 4),
            rows.Sum(r => r.DayChange),
            rows.Sum(r => r.UnrealizedPnl),
            closed.Sum(f => f.RealizedPnl ?? 0),
            fills.Sum(f => f.Commission),
            closed.Count,
            closed.Count == 0 ? null : Math.Round(closed.Count(f => f.RealizedPnl > 0) / (decimal)closed.Count * 100, 2),
            rows);
    }

    public async Task<IReadOnlyList<EquityPointDto>> GetHistoryAsync(Guid userId, CancellationToken ct = default)
    {
        var points = await db.Snapshots.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.Timestamp)
            .Select(s => new EquityPointDto(s.Timestamp, s.Equity))
            .ToListAsync(ct);

        var current = await GetAsync(userId, ct);
        points.Add(new EquityPointDto(DateTime.UtcNow, current.Equity));
        return points;
    }

    /// <summary>Records current equity, skipping the write if it hasn't changed since the last snapshot.</summary>
    public async Task RecordSnapshotAsync(Guid userId, CancellationToken ct = default)
    {
        var equity = (await GetAsync(userId, ct)).Equity;
        var last = await db.Snapshots.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.Timestamp)
            .FirstOrDefaultAsync(ct);
        if (last?.Equity == equity) return;

        db.Snapshots.Add(new PortfolioSnapshot { UserId = userId, Equity = equity });
        await db.SaveChangesAsync(ct);
    }

    private static PositionDto ToRow(Position p, Quote? q, IReadOnlyList<Fill> fills)
    {
        // When a quote is missing, value at cost and flag it rather than showing a false 100% loss.
        var last = q?.Price ?? p.AverageCost;
        var dayBase = q is null ? last : DayBasePrice(p, q, fills);
        var value = p.Quantity * last;
        var basis = p.Quantity * p.AverageCost;
        var unrealized = value - basis;

        return new PositionDto(
            p.Symbol, q?.Name ?? p.Symbol, p.Quantity, p.AverageCost, last, dayBase, value, basis, unrealized,
            basis == 0 ? 0 : Math.Round(unrealized / basis * 100, 4),
            p.Quantity * (last - dayBase),
            q is null);
    }

    /// <summary>
    /// The price today's gain or loss is measured from: shares held before this session count from the
    /// previous close, and shares bought during it count from what was paid. Without this, a stock bought
    /// a moment ago would show the whole day's move as the holder's own gain or loss.
    /// </summary>
    private static decimal DayBasePrice(Position p, Quote q, IReadOnlyList<Fill> fills)
    {
        var sessionBuys = fills
            .Where(f => f.Symbol == p.Symbol && f.Side == OrderSide.Buy && f.ExecutedAt >= (q.SessionStart ?? DateTime.MaxValue))
            .ToList();
        var boughtQuantity = sessionBuys.Sum(f => f.Quantity);
        if (boughtQuantity == 0) return q.PreviousClose;

        // Sales are treated as coming from the older shares first.
        var kept = Math.Min(boughtQuantity, p.Quantity);
        var paid = sessionBuys.Sum(f => f.Quantity * f.Price) / boughtQuantity;
        return ((p.Quantity - kept) * q.PreviousClose + kept * paid) / p.Quantity;
    }
}
