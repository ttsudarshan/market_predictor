using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Domain;

namespace PaperTrading.Api.MarketData;

/// <summary>
/// Reads Yahoo Finance's public endpoints, which are unofficial and can change or rate-limit without notice.
/// Prices come from two places: a batch quote every few seconds for the full picture, and <see cref="YahooLiveStream"/>
/// for each trade as it happens. Both end up in one record per symbol, and the newest trade always wins.
/// </summary>
public sealed class YahooMarketDataProvider(
    IHttpClientFactory httpFactory, IMemoryCache cache, IOptions<MarketDataOptions> options, IMarketClock clock,
    ILogger<YahooMarketDataProvider> log)
    : IMarketDataProvider
{
    public const string HttpClientName = "yahoo";

    private const int BatchSize = 50;
    private const int MoversPerList = 8;
    private static readonly string[] IndexSymbols = ["^GSPC", "^IXIC", "^DJI"];
    private static readonly TimeSpan MaxStaleness = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MoversLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BatchRetryAfter = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The chart endpoint takes one symbol per call, so cap the fan-out.
    private readonly SemaphoreSlim _concurrency = new(4);
    private readonly SemaphoreSlim _moversLock = new(1, 1);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly object _quotesLock = new();
    private readonly Dictionary<string, Quote> _latest = new();
    private readonly ConcurrentDictionary<string, byte> _changed = new();
    private volatile IReadOnlyCollection<string> _liveSymbols = [];
    private YahooSession? _session;
    private DateTime _batchRetryAt = DateTime.MinValue;
    private MarketMovers? _movers;
    private DateTime _moversLoadedAt = DateTime.MinValue;

    public string Name => "Yahoo Finance";
    public bool IsSimulated => false;
    public TimeSpan PollInterval => TimeSpan.FromSeconds(options.Value.YahooPollSeconds);

    /// <summary>Set by <see cref="YahooLiveStream"/> while it's connected.</summary>
    public bool IsStreaming { get; set; }

    /// <summary>The symbols the live stream should follow.</summary>
    public IReadOnlyCollection<string> LiveSymbols => _liveSymbols;

    public void SetLiveSymbols(IEnumerable<string> symbols) => _liveSymbols = symbols.ToHashSet();

    public IReadOnlyList<Quote> TakeLiveUpdates()
    {
        var updates = new List<Quote>();
        foreach (var symbol in _changed.Keys)
        {
            _changed.TryRemove(symbol, out _);
            lock (_quotesLock)
            {
                if (_latest.TryGetValue(symbol, out var quote)) updates.Add(quote);
            }
        }
        return updates;
    }

    public async Task<Quote?> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        var key = CacheKey(symbol);
        if (cache.TryGetValue(key, out bool known)) return known ? Latest(symbol) : null;

        try
        {
            await RefreshAsync([symbol], ct, rethrow: true);
        }
        catch (MarketDataUnavailableException) when (Latest(symbol) is { } last && DateTime.UtcNow - last.AsOf < MaxStaleness)
        {
            return last;
        }
        return cache.TryGetValue(key, out known) && known ? Latest(symbol) : null;
    }

    public async Task<IReadOnlyDictionary<string, Quote>> GetQuotesAsync(IEnumerable<string> symbols, CancellationToken ct = default)
    {
        var wanted = symbols.Distinct().ToList();
        var stale = wanted.Where(s => !cache.TryGetValue(CacheKey(s), out bool _)).ToList();
        if (stale.Count > 0) await RefreshAsync(stale, ct, rethrow: false);

        var now = DateTime.UtcNow;
        var quotes = new Dictionary<string, Quote>();
        foreach (var symbol in wanted)
        {
            if (cache.TryGetValue(CacheKey(symbol), out bool known) && !known) continue;
            if (Latest(symbol) is { } quote && now - quote.AsOf < MaxStaleness) quotes[symbol] = quote;
        }
        return quotes;
    }

    /// <summary>Takes in one trade from the live stream.</summary>
    public void ApplyTick(PriceTick tick)
    {
        if (tick.Price <= 0) return;
        lock (_quotesLock)
        {
            // With no quote yet there's no previous close or name to go with the price. The next check adds those.
            if (!_latest.TryGetValue(tick.Symbol, out var current)) return;

            Quote updated;
            if (IsRegularSessionTrade(tick, current))
            {
                if (current.PriceTime >= tick.Time) return;
                var status = clock.StatusFor(current.Session);
                updated = current with
                {
                    Price = tick.Price,
                    PriceTime = tick.Time,
                    AsOf = DateTime.UtcNow,
                    DayHigh = current.DayHigh is { } high ? Math.Max(high, tick.Price) : tick.Price,
                    DayLow = current.DayLow is { } low ? Math.Min(low, tick.Price) : tick.Price,
                    IsMarketOpen = status.IsOpen,
                    SessionStart = status.SessionStart,
                    Market = status,
                    ExtendedPrice = null,
                    ExtendedChangePercent = null,
                    ExtendedSession = null,
                    ExtendedTime = null,
                };
            }
            else
            {
                // Trades outside the regular session are shown beside the price. They never replace it.
                if (current.ExtendedTime >= tick.Time || tick.Time <= current.PriceTime) return;
                updated = current with
                {
                    AsOf = DateTime.UtcNow,
                    ExtendedPrice = tick.Price,
                    ExtendedChangePercent = Percent(tick.Price, current.Price),
                    ExtendedSession = tick.Time < NyseCalendar.OpenUtc(NyseCalendar.DateInNewYork(tick.Time)) ? PreMarket : AfterHours,
                    ExtendedTime = tick.Time,
                };
            }
            _latest[tick.Symbol] = updated;
            _changed[tick.Symbol] = 0;
        }
    }

    public async Task<MarketMovers> GetMoversAsync(CancellationToken ct = default)
    {
        await _moversLock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            if (_movers is not null && now - _moversLoadedAt < MoversLifetime) return _movers;

            var gainers = LoadListAsync("day_gainers", ct);
            var losers = LoadListAsync("day_losers", ct);
            var active = LoadListAsync("most_actives", ct);
            var trending = LoadTrendingAsync(ct);
            await Task.WhenAll(gainers, losers, active, trending);

            var indexQuotes = await GetQuotesAsync(IndexSymbols, ct);
            var failed = gainers.Result is null || losers.Result is null || active.Result is null || trending.Result is null;
            if (failed && _movers is null && gainers.Result is null && losers.Result is null && active.Result is null && trending.Result is null)
                throw new MarketDataUnavailableException("Yahoo Finance's market lists could not be loaded.");

            // A list that failed keeps its last good version, and the page says it may be out of date.
            var movers = new MarketMovers(
                IndexSymbols.Select(s => indexQuotes.GetValueOrDefault(s)).OfType<Quote>().ToList(),
                trending.Result ?? _movers?.Trending ?? [],
                gainers.Result ?? _movers?.Gainers ?? [],
                losers.Result ?? _movers?.Losers ?? [],
                active.Result ?? _movers?.MostActive ?? [],
                now,
                failed);

            _movers = movers;
            _moversLoadedAt = now;
            return movers;
        }
        finally
        {
            _moversLock.Release();
        }
    }

    private async Task<IReadOnlyList<Quote>?> LoadListAsync(string screenerId, CancellationToken ct)
    {
        try
        {
            // Yahoo sometimes drops a row we'd filter out, so ask for a few more than are shown.
            var url = $"v1/finance/screener/predefined/saved?scrIds={screenerId}&count={MoversPerList + 4}&formatted=false";
            var body = await GetJsonAsync<ScreenerEnvelope>(url, ct);
            if (body?.Finance?.Result?.FirstOrDefault()?.Quotes is not { } rows) return null;

            var quotes = rows.Select(ToQuote).OfType<Quote>().Where(q => q.IsTradable && q.Session is null).Take(MoversPerList).ToList();
            foreach (var quote in quotes) Remember(quote);
            return quotes;
        }
        catch (MarketDataUnavailableException ex)
        {
            log.LogWarning("Market list {List} unavailable: {Message}", screenerId, ex.Message);
            return null;
        }
    }

    private async Task<IReadOnlyList<Quote>?> LoadTrendingAsync(CancellationToken ct)
    {
        try
        {
            var body = await GetJsonAsync<TrendingEnvelope>("v1/finance/trending/US?count=20", ct);
            if (body?.Finance?.Result?.FirstOrDefault()?.Quotes is not { } rows) return null;

            // Yahoo's trending list mixes in futures and foreign listings, which can't be traded here.
            var symbols = rows.Select(r => r.Symbol).OfType<string>().Where(s => Symbols.TryNormalize(s, out _)).ToList();
            var quotes = await GetQuotesAsync(symbols, ct);
            return symbols
                .Select(s => quotes.GetValueOrDefault(s))
                .OfType<Quote>()
                .Where(q => q.IsTradable && q.Session is null)
                .Take(MoversPerList)
                .ToList();
        }
        catch (MarketDataUnavailableException ex)
        {
            log.LogWarning("Trending list unavailable: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>Fetches quotes, in one request where possible, and stores them.</summary>
    private async Task RefreshAsync(IReadOnlyList<string> symbols, CancellationToken ct, bool rethrow)
    {
        var remaining = symbols.ToHashSet();
        foreach (var quote in await FetchBatchAsync(symbols, ct))
        {
            Remember(quote);
            remaining.Remove(quote.Symbol);
        }

        // The batch can't say what an exchange's hours are, and might be unavailable, so the rest go one at a time.
        await Task.WhenAll(remaining.Select(async symbol =>
        {
            try
            {
                var chart = await FetchChartAsync(symbol, "1d", "1d", ct);
                if (chart is not null && ToQuote(symbol, chart.Meta) is { } quote)
                {
                    Remember(quote);
                    return;
                }
                // Unknown symbols are remembered longer so a bad ticker can't cause repeated upstream calls.
                cache.Set(CacheKey(symbol), false, TimeSpan.FromMinutes(5));
            }
            catch (MarketDataUnavailableException ex) when (!rethrow)
            {
                log.LogWarning("Quote for {Symbol} unavailable: {Message}", symbol, ex.Message);
            }
        }));
    }

    private async Task<IReadOnlyList<Quote>> FetchBatchAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        if (DateTime.UtcNow < _batchRetryAt) return [];
        try
        {
            var session = await GetSessionAsync(ct);
            if (session is null) return PauseBatch("no session");

            var quotes = new List<Quote>();
            foreach (var chunk in symbols.Chunk(BatchSize))
            {
                var list = string.Join(",", chunk.Select(Uri.EscapeDataString));
                var url = $"v7/finance/quote?symbols={list}&formatted=false&crumb={Uri.EscapeDataString(session.Crumb)}";
                var body = await GetJsonAsync<QuoteEnvelope>(url, ct, session.Cookie);
                if (body?.QuoteResponse?.Result is not { } rows) return PauseBatch("an unexpected response");
                quotes.AddRange(rows.Select(ToQuote).OfType<Quote>());
            }
            // Other exchanges need the session times that only the chart endpoint gives.
            return quotes.Where(q => q.Session is null).ToList();
        }
        catch (MarketDataUnavailableException ex)
        {
            return PauseBatch(ex.Message);
        }
    }

    private IReadOnlyList<Quote> PauseBatch(string reason)
    {
        log.LogWarning("Batch quotes unavailable, using single quotes for now: {Reason}", reason);
        _session = null;
        _batchRetryAt = DateTime.UtcNow + BatchRetryAfter;
        return [];
    }

    /// <summary>The batch endpoint needs a cookie and a token from Yahoo, which last for a long time.</summary>
    private async Task<YahooSession?> GetSessionAsync(CancellationToken ct)
    {
        if (_session is { } existing) return existing;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_session is { } made) return made;

            var http = httpFactory.CreateClient(HttpClientName);
            // This address answers 404 on purpose. All that's wanted from it is the cookie.
            using var cookieResponse = await http.GetAsync("https://fc.yahoo.com/", ct);
            if (!cookieResponse.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
            var cookie = string.Join("; ", cookies.Select(c => c.Split(';')[0]));

            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/test/getcrumb");
            request.Headers.Add("Cookie", cookie);
            using var crumbResponse = await http.SendAsync(request, ct);
            var crumb = await crumbResponse.Content.ReadAsStringAsync(ct);
            if (!crumbResponse.IsSuccessStatusCode || string.IsNullOrWhiteSpace(crumb) || crumb.Length > 100) return null;

            return _session = new YahooSession(cookie, crumb.Trim());
        }
        catch (HttpRequestException ex)
        {
            throw new MarketDataUnavailableException("Yahoo Finance could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MarketDataUnavailableException("Yahoo Finance timed out.", ex);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>Stores a fetched quote. If the stream has already seen a newer trade, that price stays.</summary>
    private void Remember(Quote fresh)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(options.Value.YahooPollSeconds - 1, 1));
        cache.Set(CacheKey(fresh.Symbol), true, ttl);
        lock (_quotesLock)
        {
            if (_latest.TryGetValue(fresh.Symbol, out var current))
            {
                if (current.PriceTime > fresh.PriceTime)
                {
                    fresh = fresh with
                    {
                        Price = current.Price,
                        PriceTime = current.PriceTime,
                        DayHigh = fresh.DayHigh is { } high ? Math.Max(high, current.Price) : current.DayHigh,
                        DayLow = fresh.DayLow is { } low ? Math.Min(low, current.Price) : current.DayLow,
                    };
                }
                if (current.ExtendedTime is { } streamed && !(fresh.ExtendedTime >= streamed) && streamed > fresh.PriceTime)
                {
                    fresh = fresh with
                    {
                        ExtendedPrice = current.ExtendedPrice,
                        ExtendedChangePercent = current.ExtendedChangePercent,
                        ExtendedSession = current.ExtendedSession,
                        ExtendedTime = current.ExtendedTime,
                    };
                }
            }
            _latest[fresh.Symbol] = fresh;
        }
    }

    private Quote? Latest(string symbol)
    {
        lock (_quotesLock) return _latest.GetValueOrDefault(symbol);
    }

    private static string CacheKey(string symbol) => $"q:{symbol}";

    /// <summary>
    /// Goes by the trade's own time for US stocks, as the stream leaves its session field out for pre-market trades.
    /// Other markets have only that field to go on.
    /// </summary>
    private static bool IsRegularSessionTrade(PriceTick tick, Quote quote)
    {
        if (quote.Session is not null) return tick.MarketHours == 1;
        var date = NyseCalendar.DateInNewYork(tick.Time);
        return NyseCalendar.IsTradingDay(date) && tick.Time >= NyseCalendar.OpenUtc(date) && tick.Time < NyseCalendar.CloseUtc(date);
    }

    private static decimal Percent(decimal price, decimal from) => from == 0 ? 0 : Math.Round((price - from) / from * 100, 4);

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, ChartRange range, CancellationToken ct = default)
    {
        var key = $"c:{symbol}:{range}";
        if (cache.TryGetValue(key, out IReadOnlyList<Candle>? cached) && cached is not null) return cached;

        var (yahooRange, interval) = range switch
        {
            ChartRange.OneDay => ("1d", "5m"),
            ChartRange.FiveDays => ("5d", "15m"),
            ChartRange.OneMonth => ("1mo", "60m"),
            ChartRange.SixMonths => ("6mo", "1d"),
            ChartRange.OneYear => ("1y", "1d"),
            _ => ("5y", "1wk"),
        };

        var chart = await FetchChartAsync(symbol, yahooRange, interval, ct);
        IReadOnlyList<Candle> candles = chart is null ? [] : ToCandles(chart);
        cache.Set(key, candles, TimeSpan.FromSeconds(60));
        return candles;
    }

    public async Task<IReadOnlyList<SymbolSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var key = $"s:{query.ToLowerInvariant()}";
        if (cache.TryGetValue(key, out IReadOnlyList<SymbolSearchResult>? cached) && cached is not null) return cached;

        var url = $"v1/finance/search?q={Uri.EscapeDataString(query)}&quotesCount=10&newsCount=0";
        var body = await GetJsonAsync<SearchEnvelope>(url, ct);
        IReadOnlyList<SymbolSearchResult> results = (body?.Quotes ?? [])
            .Where(q => q is { Symbol: not null, QuoteType: "EQUITY" or "ETF" })
            .Select(q => new SymbolSearchResult(q.Symbol!, q.Longname ?? q.Shortname ?? q.Symbol!, q.ExchDisp ?? q.Exchange ?? "", q.QuoteType!))
            .ToList();
        cache.Set(key, results, TimeSpan.FromMinutes(5));
        return results;
    }

    private async Task<ChartResult?> FetchChartAsync(string symbol, string range, string interval, CancellationToken ct)
    {
        var url = $"v8/finance/chart/{Uri.EscapeDataString(symbol)}?range={range}&interval={interval}";
        var body = await GetJsonAsync<ChartEnvelope>(url, ct);
        return body?.Chart?.Result?.FirstOrDefault();
    }

    /// <returns>Parsed body, or default on 404 (Yahoo's answer for an unknown symbol).</returns>
    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct, string? cookie = null)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cookie is not null) request.Headers.Add("Cookie", cookie);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return default;
            if (!response.IsSuccessStatusCode)
                throw new MarketDataUnavailableException($"Yahoo Finance returned HTTP {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MarketDataUnavailableException("Yahoo Finance could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MarketDataUnavailableException("Yahoo Finance timed out.", ex);
        }
        catch (JsonException ex)
        {
            throw new MarketDataUnavailableException("Yahoo Finance returned an unexpected response.", ex);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private Quote? ToQuote(string symbol, ChartMeta meta)
    {
        if (meta.RegularMarketPrice is not { } price) return null;

        var session = ForeignSession(meta);
        var status = clock.StatusFor(session);

        return new Quote(
            symbol,
            meta.LongName ?? meta.ShortName ?? symbol,
            meta.Currency ?? "USD",
            meta.FullExchangeName ?? meta.ExchangeName ?? "",
            meta.InstrumentType ?? "",
            Round(price),
            Round(meta.ChartPreviousClose ?? meta.PreviousClose ?? price),
            meta.RegularMarketDayHigh is { } h ? Round(h) : null,
            meta.RegularMarketDayLow is { } l ? Round(l) : null,
            meta.RegularMarketVolume,
            meta.FiftyTwoWeekHigh is { } wh ? Round(wh) : null,
            meta.FiftyTwoWeekLow is { } wl ? Round(wl) : null,
            status.IsOpen,
            DateTime.UtcNow,
            status.SessionStart,
            meta.RegularMarketTime is { } traded ? DateTimeOffset.FromUnixTimeSeconds(traded).UtcDateTime : null,
            status,
            session);
    }

    private Quote? ToQuote(QuoteRow row)
    {
        if (row.Symbol is null || row.RegularMarketPrice is not { } price) return null;

        // Foreign quotes from here are only used to spot them. The chart endpoint supplies their real hours.
        var session = row.ExchangeTimezoneName is null or NyseCalendar.TimeZoneId
            ? null
            : new ExchangeSession(row.ExchangeTimezoneName, DateTime.MinValue, DateTime.MinValue);
        var status = clock.StatusFor(session);
        var priceTime = row.RegularMarketTime is { } traded ? FromUnix(traded) : (DateTime?)null;
        var rounded = Round(price);

        // Whichever of pre-market and after hours is newer, as long as it came after the regular session.
        var (extended, extendedTime, label) = row.PreMarketTime > row.PostMarketTime || row.PostMarketTime is null
            ? (row.PreMarketPrice, row.PreMarketTime, PreMarket)
            : (row.PostMarketPrice, row.PostMarketTime, AfterHours);
        var hasExtended = extended is not null && extendedTime is { } t && priceTime is { } p && FromUnix(t) > p;

        return new Quote(
            row.Symbol,
            row.LongName ?? row.ShortName ?? row.Symbol,
            row.Currency ?? "USD",
            row.FullExchangeName ?? row.Exchange ?? "",
            row.QuoteType ?? "",
            rounded,
            Round(row.RegularMarketPreviousClose ?? price),
            row.RegularMarketDayHigh is { } h ? Round(h) : null,
            row.RegularMarketDayLow is { } l ? Round(l) : null,
            row.RegularMarketVolume,
            row.FiftyTwoWeekHigh is { } wh ? Round(wh) : null,
            row.FiftyTwoWeekLow is { } wl ? Round(wl) : null,
            status.IsOpen,
            DateTime.UtcNow,
            status.SessionStart,
            priceTime,
            status,
            session,
            hasExtended ? Round(extended!.Value) : null,
            hasExtended ? Percent(Round(extended!.Value), rounded) : null,
            hasExtended ? label : null,
            hasExtended ? FromUnix(extendedTime!.Value) : null);
    }

    private static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    /// <summary>US listings follow the NYSE calendar. Other exchanges have their own hours, which Yahoo reports.</summary>
    private static ExchangeSession? ForeignSession(ChartMeta meta)
    {
        if (meta.ExchangeTimezoneName is null or NyseCalendar.TimeZoneId || meta.CurrentTradingPeriod?.Regular is not { } regular) return null;
        return new ExchangeSession(
            meta.ExchangeTimezoneName,
            DateTimeOffset.FromUnixTimeSeconds(regular.Start).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(regular.End).UtcDateTime);
    }

    private static List<Candle> ToCandles(ChartResult chart)
    {
        var q = chart.Indicators?.Quote?.FirstOrDefault();
        if (q is null || chart.Timestamp is null) return [];

        var candles = new List<Candle>(chart.Timestamp.Length);
        for (var i = 0; i < chart.Timestamp.Length; i++)
        {
            // Yahoo leaves nulls in the arrays for intervals with no trades.
            if (i >= q.Open?.Length || i >= q.Close?.Length || i >= q.High?.Length || i >= q.Low?.Length) break;
            if (q.Open?[i] is not { } open || q.High?[i] is not { } high ||
                q.Low?[i] is not { } low || q.Close?[i] is not { } close) continue;

            candles.Add(new Candle(chart.Timestamp[i], Round(open), Round(high), Round(low), Round(close), q.Volume?.ElementAtOrDefault(i) ?? 0));
        }
        return candles;
    }

    // Yahoo's OHLC values are floats like 344.6300048828125.
    private static decimal Round(decimal value) => Math.Round(value, 4);

    private const string PreMarket = "Pre-market";
    private const string AfterHours = "After hours";

    private sealed record YahooSession(string Cookie, string Crumb);
    private sealed record QuoteEnvelope(QuoteResponseBody? QuoteResponse);
    private sealed record QuoteResponseBody(QuoteRow[]? Result);
    private sealed record ScreenerEnvelope(ScreenerFinance? Finance);
    private sealed record ScreenerFinance(ScreenerResult[]? Result);
    private sealed record ScreenerResult(QuoteRow[]? Quotes);
    private sealed record TrendingEnvelope(TrendingFinance? Finance);
    private sealed record TrendingFinance(TrendingResult[]? Result);
    private sealed record TrendingResult(TrendingRow[]? Quotes);
    private sealed record TrendingRow(string? Symbol);
    private sealed record QuoteRow(
        string? Symbol, string? QuoteType, string? Currency, string? ShortName, string? LongName, string? FullExchangeName, string? Exchange,
        string? ExchangeTimezoneName, decimal? RegularMarketPrice, decimal? RegularMarketPreviousClose, long? RegularMarketTime,
        decimal? RegularMarketDayHigh, decimal? RegularMarketDayLow, long? RegularMarketVolume, decimal? FiftyTwoWeekHigh, decimal? FiftyTwoWeekLow,
        decimal? PreMarketPrice, long? PreMarketTime, decimal? PostMarketPrice, long? PostMarketTime);
    private sealed record ChartEnvelope(ChartBody? Chart);
    private sealed record ChartBody(ChartResult[]? Result);
    private sealed record ChartResult(ChartMeta Meta, long[]? Timestamp, Indicators? Indicators);
    private sealed record Indicators(OhlcSeries[]? Quote);
    private sealed record OhlcSeries(decimal?[]? Open, decimal?[]? High, decimal?[]? Low, decimal?[]? Close, long?[]? Volume);
    private sealed record ChartMeta(
        string? Currency, string? ExchangeName, string? FullExchangeName, string? InstrumentType,
        decimal? RegularMarketPrice, decimal? ChartPreviousClose, decimal? PreviousClose,
        decimal? RegularMarketDayHigh, decimal? RegularMarketDayLow, long? RegularMarketVolume,
        decimal? FiftyTwoWeekHigh, decimal? FiftyTwoWeekLow, string? LongName, string? ShortName,
        TradingPeriods? CurrentTradingPeriod, long? RegularMarketTime, string? ExchangeTimezoneName);
    private sealed record TradingPeriods(TradingPeriod? Regular);
    private sealed record TradingPeriod(long Start, long End);
    private sealed record SearchEnvelope(SearchQuote[]? Quotes);
    private sealed record SearchQuote(
        string? Symbol, string? Shortname, string? Longname, string? QuoteType, string? Exchange, string? ExchDisp);
}
