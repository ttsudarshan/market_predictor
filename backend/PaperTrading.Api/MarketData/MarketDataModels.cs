namespace PaperTrading.Api.MarketData;

public record Quote(
    string Symbol, string Name, string Currency, string Exchange, string InstrumentType,
    decimal Price, decimal PreviousClose, decimal? DayHigh, decimal? DayLow, long? Volume,
    decimal? FiftyTwoWeekHigh, decimal? FiftyTwoWeekLow, bool IsMarketOpen, DateTime AsOf,
    DateTime? SessionStart = null, DateTime? PriceTime = null, MarketStatus? Market = null,
    [property: System.Text.Json.Serialization.JsonIgnore] ExchangeSession? Session = null,
    decimal? ExtendedPrice = null, decimal? ExtendedChangePercent = null, string? ExtendedSession = null, DateTime? ExtendedTime = null)
{
    public decimal Change => Price - PreviousClose;
    public decimal ChangePercent => PreviousClose == 0 ? 0 : Math.Round(Change / PreviousClose * 100, 4);
    public bool IsTradable => InstrumentType is "EQUITY" or "ETF";
}

public record Candle(long Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

public record SymbolSearchResult(string Symbol, string Name, string Exchange, string Type);

/// <summary>What's moving. Lists keep the order the source ranks them in.</summary>
/// <param name="Stale">True if a list couldn't be refreshed and an older version is being shown.</param>
public record MarketMovers(
    IReadOnlyList<Quote> Indexes, IReadOnlyList<Quote> Trending, IReadOnlyList<Quote> Gainers,
    IReadOnlyList<Quote> Losers, IReadOnlyList<Quote> MostActive, DateTime LoadedAt, bool Stale);

public interface IMarketDataProvider
{
    string Name { get; }
    bool IsSimulated { get; }

    /// <summary>How often the background loop should refresh prices.</summary>
    TimeSpan PollInterval { get; }

    /// <returns>The quote, or null when the symbol does not exist.</returns>
    /// <exception cref="Domain.MarketDataUnavailableException">The feed could not answer.</exception>
    Task<Quote?> GetQuoteAsync(string symbol, CancellationToken ct = default);

    /// <summary>Best effort: symbols that are unknown or fail to load are left out of the result.</summary>
    Task<IReadOnlyDictionary<string, Quote>> GetQuotesAsync(IEnumerable<string> symbols, CancellationToken ct = default);

    Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, ChartRange range, CancellationToken ct = default);

    Task<IReadOnlyList<SymbolSearchResult>> SearchAsync(string query, CancellationToken ct = default);

    /// <exception cref="Domain.MarketDataUnavailableException">The lists couldn't be loaded and there's no earlier copy.</exception>
    Task<MarketMovers> GetMoversAsync(CancellationToken ct = default);

    /// <summary>True if prices are being pushed as trades happen, rather than only fetched on a timer.</summary>
    bool IsStreaming => false;

    /// <summary>Tells a streaming feed which symbols to follow.</summary>
    void SetLiveSymbols(IEnumerable<string> symbols) { }

    /// <summary>The quotes that streamed in since the last call.</summary>
    IReadOnlyList<Quote> TakeLiveUpdates() => [];
}

public enum ChartRange { OneDay, FiveDays, OneMonth, SixMonths, OneYear, FiveYears }

public static class ChartRanges
{
    public static ChartRange Parse(string? value) => value?.ToUpperInvariant() switch
    {
        "1D" => ChartRange.OneDay,
        "5D" => ChartRange.FiveDays,
        "1M" => ChartRange.OneMonth,
        "6M" => ChartRange.SixMonths,
        "1Y" or null or "" => ChartRange.OneYear,
        "5Y" => ChartRange.FiveYears,
        _ => throw new Domain.TradingException($"Unknown chart range '{value}'."),
    };
}

public class MarketDataOptions
{
    /// <summary>"Yahoo" for real prices or "Simulated" for a random walk.</summary>
    public string Provider { get; set; } = "Yahoo";
    public int YahooPollSeconds { get; set; } = 15;
    public int SimulatedPollSeconds { get; set; } = 2;

    /// <summary>Per-tick volatility of the simulated walk, as a fraction of price.</summary>
    public double SimulatedVolatility { get; set; } = 0.0012;

    /// <summary>Start simulated prices from real Yahoo prices when reachable.</summary>
    public bool SeedSimulationFromYahoo { get; set; } = true;
}
