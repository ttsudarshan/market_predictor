using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Domain;

namespace PaperTrading.Api.MarketData;

/// <summary>
/// A random-walk market that is always open, for trying orders at weekends or with no network.
/// Prices start from real Yahoo prices when reachable, otherwise from the table below.
/// </summary>
public sealed class SimulatedMarketDataProvider(
    YahooMarketDataProvider yahoo, IOptions<MarketDataOptions> options, ILogger<SimulatedMarketDataProvider> log)
    : IMarketDataProvider
{
    private static readonly (string Symbol, string Name, string Type, decimal Price)[] Universe =
    [
        ("AAPL", "Apple Inc.", "EQUITY", 340m), ("MSFT", "Microsoft Corporation", "EQUITY", 510m),
        ("NVDA", "NVIDIA Corporation", "EQUITY", 180m), ("GOOGL", "Alphabet Inc.", "EQUITY", 250m),
        ("AMZN", "Amazon.com, Inc.", "EQUITY", 225m), ("META", "Meta Platforms, Inc.", "EQUITY", 700m),
        ("TSLA", "Tesla, Inc.", "EQUITY", 420m), ("AMD", "Advanced Micro Devices, Inc.", "EQUITY", 210m),
        ("NFLX", "Netflix, Inc.", "EQUITY", 1100m), ("JPM", "JPMorgan Chase & Co.", "EQUITY", 300m),
        ("V", "Visa Inc.", "EQUITY", 340m), ("KO", "The Coca-Cola Company", "EQUITY", 70m),
        ("DIS", "The Walt Disney Company", "EQUITY", 110m), ("BA", "The Boeing Company", "EQUITY", 215m),
        ("SPY", "SPDR S&P 500 ETF Trust", "ETF", 680m), ("QQQ", "Invesco QQQ Trust", "ETF", 610m),
        ("VTI", "Vanguard Total Stock Market ETF", "ETF", 330m),
    ];

    private const int MoversPerList = 8;
    private readonly ConcurrentDictionary<string, Instrument> _instruments = new();

    public string Name => "Simulated market";
    public bool IsSimulated => true;
    public TimeSpan PollInterval => TimeSpan.FromSeconds(options.Value.SimulatedPollSeconds);

    public async Task<Quote?> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        var instrument = await GetInstrumentAsync(symbol, ct);
        return instrument?.Advance(PollInterval, options.Value.SimulatedVolatility);
    }

    public async Task<IReadOnlyDictionary<string, Quote>> GetQuotesAsync(IEnumerable<string> symbols, CancellationToken ct = default)
    {
        var quotes = await Task.WhenAll(symbols.Distinct().Select(s => GetQuoteAsync(s, ct)));
        return quotes.OfType<Quote>().ToDictionary(q => q.Symbol);
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, ChartRange range, CancellationToken ct = default)
    {
        var quote = await GetQuoteAsync(symbol, ct);
        if (quote is null) return [];

        var (count, stepSeconds, volatility) = range switch
        {
            ChartRange.OneDay => (78, 300, 0.0012),
            ChartRange.FiveDays => (130, 900, 0.002),
            ChartRange.OneMonth => (150, 3600, 0.004),
            ChartRange.SixMonths => (126, 86400, 0.014),
            ChartRange.OneYear => (252, 86400, 0.014),
            _ => (260, 604800, 0.03),
        };

        // History is seeded by symbol so it stays the same between requests, and is walked backwards
        // from the live price so the last candle always lines up with the current quote.
        var random = new Random(StableHash(symbol) ^ (int)range);
        var candles = new Candle[count];
        var close = (double)quote.Price;
        var lastBar = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / stepSeconds * stepSeconds;

        for (var i = count - 1; i >= 0; i--)
        {
            var open = close * Math.Exp(NextGaussian(random) * volatility);
            var high = Math.Max(open, close) * (1 + random.NextDouble() * volatility / 2);
            var low = Math.Min(open, close) * (1 - random.NextDouble() * volatility / 2);
            candles[i] = new Candle(
                lastBar - (count - 1 - i) * stepSeconds,
                Money(open), Money(high), Money(low), Money(close),
                random.NextInt64(100_000, 5_000_000));
            close = open;
        }
        return candles;
    }

    public async Task<IReadOnlyList<SymbolSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var local = Universe
            .Where(u => u.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || u.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(u => new SymbolSearchResult(u.Symbol, u.Name, "SIM", u.Type))
            .ToList();
        if (local.Count > 0 || !options.Value.SeedSimulationFromYahoo) return local;

        try { return await yahoo.SearchAsync(query, ct); }
        catch (MarketDataUnavailableException) { return local; }
    }

    public async Task<MarketMovers> GetMoversAsync(CancellationToken ct = default)
    {
        var quotes = (await GetQuotesAsync(Universe.Select(u => u.Symbol), ct)).Values.ToList();
        return new MarketMovers(
            [],
            Universe.Take(MoversPerList).Select(u => quotes.Find(q => q.Symbol == u.Symbol)).OfType<Quote>().ToList(),
            quotes.Where(q => q.Change > 0).OrderByDescending(q => q.ChangePercent).Take(MoversPerList).ToList(),
            quotes.Where(q => q.Change < 0).OrderBy(q => q.ChangePercent).Take(MoversPerList).ToList(),
            quotes.OrderByDescending(q => q.Volume).Take(MoversPerList).ToList(),
            DateTime.UtcNow,
            Stale: false);
    }

    private async Task<Instrument?> GetInstrumentAsync(string symbol, CancellationToken ct)
    {
        if (_instruments.TryGetValue(symbol, out var existing)) return existing;

        Quote? seed = null;
        if (options.Value.SeedSimulationFromYahoo)
        {
            try { seed = await yahoo.GetQuoteAsync(symbol, ct); }
            catch (MarketDataUnavailableException ex) { log.LogInformation("Simulating {Symbol} without a real seed: {Message}", symbol, ex.Message); }
        }

        Instrument? created = null;
        if (seed is not null)
        {
            created = new Instrument(symbol, seed.Name, seed.InstrumentType, seed.Currency, seed.Price);
        }
        else
        {
            var match = Universe.FirstOrDefault(u => u.Symbol == symbol);
            if (match.Symbol is not null)
                created = new Instrument(symbol, match.Name, match.Type, "USD", match.Price);
        }
        return created is null ? null : _instruments.GetOrAdd(symbol, created);
    }

    private static int StableHash(string s) => s.Aggregate(17, (h, c) => unchecked(h * 31 + c));

    private static double NextGaussian(Random r) =>
        Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    private static decimal Money(double v) => Math.Round((decimal)Math.Max(v, 0.01), 2);

    private sealed class Instrument(string symbol, string name, string type, string currency, decimal start)
    {
        private readonly object _lock = new();
        private readonly decimal _previousClose = start;
        private readonly DateTime _sessionStart = DateTime.UtcNow;
        private decimal _price = start, _high = start, _low = start;
        private long _volume;
        private DateTime _lastTick = DateTime.MinValue;

        public Quote Advance(TimeSpan tick, double volatility)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                // Slightly under the full tick, so timer jitter doesn't make the loop skip a step.
                if (_lastTick != DateTime.MinValue && now - _lastTick >= tick * 0.9)
                {
                    _price = Money((double)_price * Math.Exp(NextGaussian(Random.Shared) * volatility));
                    _high = Math.Max(_high, _price);
                    _low = Math.Min(_low, _price);
                    _volume += Random.Shared.NextInt64(500, 20_000);
                    _lastTick = now;
                }
                else if (_lastTick == DateTime.MinValue)
                {
                    _lastTick = now;
                }

                return new Quote(symbol, name, currency, "Simulated", type, _price, _previousClose, _high, _low, _volume,
                    null, null, IsMarketOpen: true, AsOf: now, SessionStart: _sessionStart, PriceTime: now, Market: AlwaysOpenClock.Open);
            }
        }
    }
}
