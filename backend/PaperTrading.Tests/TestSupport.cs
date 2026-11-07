using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;
using PaperTrading.Api.Insights;
using PaperTrading.Api.MarketData;
using PaperTrading.Api.Services;

namespace PaperTrading.Tests;

/// <summary>A market whose prices the test sets directly.</summary>
public sealed class FakeMarket : IMarketDataProvider
{
    private readonly Dictionary<string, (decimal Price, string Currency, string Type, decimal? PreviousClose)> _prices = new();

    /// <summary>When the current session began. Fills from before this count as held overnight.</summary>
    public DateTime SessionStart { get; set; } = DateTime.UtcNow.AddHours(-1);
    private readonly object _lock = new();

    /// <summary>When the last trade happened. Null means just now.</summary>
    public DateTime? PriceTime { get; set; }

    public bool Failing { get; set; }
    public string Name => "Fake";
    public bool IsSimulated => true;
    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(100);

    public void Set(string symbol, decimal price, string currency = "USD", string type = "EQUITY", decimal? previousClose = null)
    {
        lock (_lock) _prices[symbol] = (price, currency, type, previousClose);
    }

    public Task<Quote?> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        if (Failing) throw new MarketDataUnavailableException("Feed down.");
        lock (_lock)
        {
            return Task.FromResult(_prices.TryGetValue(symbol, out var p)
                ? new Quote(symbol, symbol, p.Currency, "TEST", p.Type, p.Price, p.PreviousClose ?? p.Price, null, null, null, null, null, true, DateTime.UtcNow, SessionStart, PriceTime ?? DateTime.UtcNow)
                : null);
        }
    }

    public async Task<IReadOnlyDictionary<string, Quote>> GetQuotesAsync(IEnumerable<string> symbols, CancellationToken ct = default)
    {
        var quotes = await Task.WhenAll(symbols.Select(s => GetQuoteAsync(s, ct)));
        return quotes.OfType<Quote>().ToDictionary(q => q.Symbol);
    }

    public MarketMovers Movers { get; set; } = new([], [], [], [], [], DateTime.UtcNow, false);

    public Task<MarketMovers> GetMoversAsync(CancellationToken ct = default) => Task.FromResult(Movers);

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, ChartRange range, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Candle>>([]);

    public Task<IReadOnlyList<SymbolSearchResult>> SearchAsync(string query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SymbolSearchResult>>([]);
}

/// <summary>A clock a test can set and move.</summary>
public sealed class TestTime(DateTime utc) : TimeProvider
{
    public DateTime Now { get; set; } = utc;
    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

/// <summary>Real services over a private in-memory SQLite database, so decimal handling matches production.</summary>
public sealed class Harness : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public FakeMarket Market { get; } = new();
    public AppDbContext Db { get; }
    public OrderService Orders { get; }
    public PortfolioService Portfolio { get; }
    public AccountService Accounts { get; }
    public Guid UserId { get; }

    public Harness(decimal cash = 10_000m, decimal commission = 0m, IMarketClock? clock = null)
    {
        _connection.Open();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        Db.Database.EnsureCreated();

        var trading = Options.Create(new TradingOptions { CommissionPerOrder = commission, DefaultStartingBalance = cash });
        var gate = new ExecutionGate();
        Orders = new OrderService(Db, Market, clock ?? new AlwaysOpenClock(), gate, trading);
        Portfolio = new PortfolioService(Db, Market, Orders);
        Accounts = new AccountService(Db, gate, trading, Options.Create(new JwtSettings { Key = new string('k', 48) }));

        var user = new User { Email = "t@example.com", DisplayName = "T", Cash = cash, StartingBalance = cash };
        Db.Users.Add(user);
        Db.SaveChanges();
        UserId = user.Id;
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "papertrading-tests-" + Guid.NewGuid());

    public FakeMarket Market { get; } = new();
    public FakeYouTube YouTube { get; } = new();
    public FakeLlm Llm { get; } = new();
    public FakePrices Prices { get; } = new();

    /// <summary>Open unless a test says otherwise, so results don't depend on when the tests run.</summary>
    public IMarketClock Clock { get; set; } = new AlwaysOpenClock();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("DataDirectory", _dataDirectory);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMarketDataProvider>();
            services.AddSingleton<IMarketDataProvider>(Market);
            services.RemoveAll<IMarketClock>();
            services.AddSingleton<IMarketClock>(_ => Clock);
            // Tests must never reach YouTube, Yahoo or the model.
            services.RemoveAll<IYouTubeGateway>();
            services.AddSingleton<IYouTubeGateway>(YouTube);
            services.RemoveAll<ILlmClient>();
            services.AddSingleton<ILlmClient>(Llm);
            services.RemoveAll<IPriceHistory>();
            services.AddSingleton<IPriceHistory>(Prices);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        if (disposing && Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }
}

internal static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList()) services.Remove(d);
    }
}
