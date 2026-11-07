using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Contracts;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;
using PaperTrading.Api.MarketData;
using PaperTrading.Api.Services;

namespace PaperTrading.Api.Controllers;

[ApiController]
[Authorize]
public abstract class ApiController : ControllerBase
{
    protected Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

[Route("api/auth")]
public sealed class AuthController(AccountService accounts) : ApiController
{
    [HttpPost("register"), AllowAnonymous, EnableRateLimiting("auth")]
    public Task<AuthResponse> Register(RegisterRequest request, CancellationToken ct) => accounts.RegisterAsync(request, ct);

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("auth")]
    public Task<AuthResponse> Login(LoginRequest request, CancellationToken ct) => accounts.LoginAsync(request, ct);

    [HttpGet("me")]
    public Task<UserDto> Me(CancellationToken ct) => accounts.GetAsync(UserId, ct);
}

[Route("api/market")]
public sealed class MarketController(IMarketDataProvider market, IOptions<TradingOptions> trading) : ApiController
{
    [HttpGet("info"), AllowAnonymous]
    public MarketInfoDto Info() => new(
        market.Name, market.IsSimulated, trading.Value.BaseCurrency, trading.Value.DefaultStartingBalance,
        trading.Value.CommissionPerOrder, (int)market.PollInterval.TotalSeconds, market.IsStreaming);

    [HttpGet("status"), AllowAnonymous]
    public MarketStatus Status([FromServices] IMarketClock clock) => clock.Status();

    [HttpGet("movers")]
    public Task<MarketMovers> Movers(CancellationToken ct) => market.GetMoversAsync(ct);

    [HttpGet("quote/{symbol}")]
    public async Task<ActionResult<Quote>> GetQuote(string symbol, CancellationToken ct)
    {
        var normalized = Symbols.Normalize(symbol);
        var quote = await market.GetQuoteAsync(normalized, ct);
        return quote is null ? NotFound(new ProblemDetails { Title = $"Symbol {normalized} was not found." }) : quote;
    }

    [HttpGet("candles/{symbol}")]
    public async Task<IReadOnlyList<Candle>> GetCandles(string symbol, [FromQuery] string? range, CancellationToken ct) =>
        await market.GetCandlesAsync(Symbols.Normalize(symbol), ChartRanges.Parse(range), ct);

    [HttpGet("search")]
    public async Task<IReadOnlyList<SymbolSearchResult>> Search([FromQuery] string? q, CancellationToken ct)
    {
        var query = q?.Trim() ?? "";
        return query.Length is 0 or > 40 ? [] : await market.SearchAsync(query, ct);
    }
}

[Route("api/orders")]
public sealed class OrdersController(AppDbContext db, OrderService orders) : ApiController
{
    [HttpGet]
    public async Task<IReadOnlyList<OrderDto>> List([FromQuery] bool openOnly, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking().Where(o => o.UserId == UserId);
        if (openOnly) query = query.Where(o => o.Status == OrderStatus.Open);
        var rows = await query.OrderByDescending(o => o.CreatedAt).Take(500).ToListAsync(ct);
        return rows.Select(OrderDto.From).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<OrderDto>> Place(PlaceOrderRequest request, CancellationToken ct)
    {
        var order = await orders.PlaceAsync(UserId, request, ct);
        return Created($"/api/orders/{order.Id}", OrderDto.From(order));
    }

    [HttpDelete("{id:guid}")]
    public async Task<OrderDto> Cancel(Guid id, CancellationToken ct) =>
        OrderDto.From(await orders.CancelAsync(UserId, id, ct));
}

[Route("api/portfolio")]
public sealed class PortfolioController(AppDbContext db, PortfolioService portfolio) : ApiController
{
    [HttpGet]
    public Task<PortfolioDto> Get(CancellationToken ct) => portfolio.GetAsync(UserId, ct);

    [HttpGet("history")]
    public Task<IReadOnlyList<EquityPointDto>> History(CancellationToken ct) => portfolio.GetHistoryAsync(UserId, ct);

    [HttpGet("fills")]
    public async Task<IReadOnlyList<FillDto>> Fills(CancellationToken ct)
    {
        var rows = await db.Fills.AsNoTracking()
            .Where(f => f.UserId == UserId)
            .OrderByDescending(f => f.ExecutedAt)
            .Take(500)
            .ToListAsync(ct);
        return rows.Select(FillDto.From).ToList();
    }
}

[Route("api/watchlists")]
public sealed class WatchlistsController(AppDbContext db, IMarketDataProvider market) : ApiController
{
    private const int MaxLists = 20, MaxSymbolsPerList = 100;

    [HttpGet]
    public async Task<IReadOnlyList<WatchListDto>> List(CancellationToken ct)
    {
        var lists = await db.WatchLists.AsNoTracking().Where(l => l.UserId == UserId).OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var ids = lists.Select(l => l.Id).ToList();
        var items = await db.WatchlistItems.AsNoTracking()
            .Where(i => ids.Contains(i.WatchListId))
            .OrderBy(i => i.AddedAt)
            .ToListAsync(ct);

        return lists
            .OrderByDescending(l => l.IsDefault)
            .Select(l => new WatchListDto(l.Id, l.Name, l.IsDefault, items.Where(i => i.WatchListId == l.Id).Select(i => i.Symbol).ToList()))
            .ToList();
    }

    [HttpPost]
    public async Task<ActionResult<WatchListDto>> Create(WatchListNameRequest request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (name.Length == 0) throw new TradingException("Give the list a name.");
        if (await db.WatchLists.CountAsync(l => l.UserId == UserId, ct) >= MaxLists)
            throw new TradingException($"You can have up to {MaxLists} lists.");
        if (await db.WatchLists.AnyAsync(l => l.UserId == UserId && l.Name == name, ct))
            throw new TradingException($"You already have a list called {name}.", StatusCodes.Status409Conflict);

        var list = new WatchList { UserId = UserId, Name = name };
        db.WatchLists.Add(list);
        await db.SaveChangesAsync(ct);
        return Created($"/api/watchlists/{list.Id}", new WatchListDto(list.Id, list.Name, false, []));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Rename(Guid id, WatchListNameRequest request, CancellationToken ct)
    {
        var list = await FindAsync(id, ct);
        var name = request.Name.Trim();
        if (name.Length == 0) throw new TradingException("Give the list a name.");
        if (await db.WatchLists.AnyAsync(l => l.UserId == UserId && l.Name == name && l.Id != id, ct))
            throw new TradingException($"You already have a list called {name}.", StatusCodes.Status409Conflict);

        list.Name = name;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var list = await FindAsync(id, ct);
        if (list.IsDefault) throw new TradingException("Your main list can't be deleted.");

        db.WatchLists.Remove(list);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/symbols")]
    public async Task<IActionResult> AddSymbol(Guid id, WatchlistAddRequest request, CancellationToken ct)
    {
        await FindAsync(id, ct);
        var symbol = Symbols.Normalize(request.Symbol);
        if (await db.WatchlistItems.AnyAsync(i => i.WatchListId == id && i.Symbol == symbol, ct)) return NoContent();
        if (await db.WatchlistItems.CountAsync(i => i.WatchListId == id, ct) >= MaxSymbolsPerList)
            throw new TradingException($"A list can hold up to {MaxSymbolsPerList} tickers.");

        if (await market.GetQuoteAsync(symbol, ct) is null)
            throw new TradingException($"Symbol {symbol} was not found.");

        db.WatchlistItems.Add(new WatchlistItem { WatchListId = id, Symbol = symbol });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/symbols/{symbol}")]
    public async Task<IActionResult> RemoveSymbol(Guid id, string symbol, CancellationToken ct)
    {
        await FindAsync(id, ct);
        var normalized = Symbols.Normalize(symbol);
        await db.WatchlistItems.Where(i => i.WatchListId == id && i.Symbol == normalized).ExecuteDeleteAsync(ct);
        return NoContent();
    }

    private async Task<WatchList> FindAsync(Guid id, CancellationToken ct) =>
        await db.WatchLists.FirstOrDefaultAsync(l => l.Id == id && l.UserId == UserId, ct)
        ?? throw new TradingException("List not found.", StatusCodes.Status404NotFound);
}

[Route("api/account")]
public sealed class AccountController(AccountService accounts) : ApiController
{
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(ResetAccountRequest request, CancellationToken ct)
    {
        await accounts.ResetAsync(UserId, request.StartingBalance, ct);
        return NoContent();
    }
}
