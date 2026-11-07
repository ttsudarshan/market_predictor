using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Contracts;
using PaperTrading.Api.Domain;
using PaperTrading.Api.MarketData;

namespace PaperTrading.Tests;

public class YahooPricingTests
{
    /// <summary>A real message from Yahoo's stream, captured on 2026-11-02.</summary>
    private const string RealBitcoinMessage =
        "CgdCVEMtVVNEFb/pmEcYwJrxrLNoIgNVU0QqA0NDQzApOAFFfFItP0iAoMT/b1W6iZlHXXs6mEdlAKADRH3IzZhHsAGAoMT/b9gBBOABgKDE/2/oAYCgxP9v8gEDQlRDgQIAAABAiSxzQYkCAADSgAzpdkL1AgCgA0T9AnxSLT8=";

    [Fact]
    public void A_real_stream_message_decodes()
    {
        var tick = YahooPricing.Decode(Convert.FromBase64String(RealBitcoinMessage));

        Assert.Equal("BTC-USD", tick!.Symbol);
        Assert.Equal(78291.49m, tick.Price);
        Assert.Equal(new DateTime(2026, 11, 2, 5, 49, 24, DateTimeKind.Utc), tick.Time);
        Assert.Equal(1, tick.MarketHours);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CgdCVEM=")] // Cut off partway through the symbol
    [InlineData("Cgc=")]
    public void A_broken_message_is_ignored_rather_than_throwing(string base64) =>
        Assert.Null(YahooPricing.Decode(Convert.FromBase64String(base64)));

    [Fact]
    public void Rubbish_never_throws()
    {
        var random = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(0, 80)];
            random.NextBytes(bytes);
            _ = YahooPricing.Decode(bytes);
        }
    }

    [Theory]
    [InlineData(344.63f, 344.63)]
    [InlineData(205.2499f, 205.2499)]
    [InlineData(0.1234f, 0.1234)]
    [InlineData(700123.06f, 700123.06)]
    public void Float_noise_is_removed_from_prices(float raw, double expected)
    {
        var tick = YahooPricing.Decode(Message("AAA", raw, 1793598564000));
        Assert.Equal((decimal)expected, tick!.Price);
    }

    /// <summary>Builds a stream message the way Yahoo does.</summary>
    internal static byte[] Message(string symbol, float price, long timeMs, int? hours = null)
    {
        var bytes = new List<byte> { 0x0A, (byte)symbol.Length };
        bytes.AddRange(Encoding.UTF8.GetBytes(symbol));
        bytes.Add(0x15);
        bytes.AddRange(BitConverter.GetBytes(price));
        bytes.Add(0x18);
        var zigzag = (ulong)timeMs << 1;
        while (zigzag >= 0x80) { bytes.Add((byte)(zigzag | 0x80)); zigzag >>= 7; }
        bytes.Add((byte)zigzag);
        if (hours is { } h) { bytes.Add(0x38); bytes.Add((byte)h); }
        return [.. bytes];
    }
}

public class LivePriceMergeTests
{
    private static readonly DateTime FridayClose = new(2026, 10, 30, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MondayOpen = new(2026, 11, 2, 14, 30, 0, DateTimeKind.Utc);
    private static long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

    private sealed class RouteHttp(Func<string, string?> route) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://query1.example.test/") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            if (url.Contains("fc.yahoo.com"))
            {
                var cookie = new HttpResponseMessage(HttpStatusCode.NotFound);
                cookie.Headers.Add("Set-Cookie", "A3=abc; Path=/");
                return Task.FromResult(cookie);
            }
            if (url.Contains("getcrumb")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("crumb") });
            var body = route(url);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static object Row(string symbol, decimal price, DateTime traded, string type = "EQUITY", string zone = "America/New_York", decimal? post = null, DateTime? postTime = null, decimal prev = 100m) => new
    {
        symbol, quoteType = type, currency = "USD", shortName = symbol, fullExchangeName = "NYSE", exchangeTimezoneName = zone,
        regularMarketPrice = price, regularMarketPreviousClose = prev, regularMarketTime = Unix(traded),
        regularMarketDayHigh = price, regularMarketDayLow = price, regularMarketVolume = 1000L,
        postMarketPrice = post, postMarketTime = postTime is { } t ? Unix(t) : (long?)null,
    };

    private static string Batch(params object[] rows) => JsonSerializer.Serialize(new { quoteResponse = new { result = rows } });

    private static (YahooMarketDataProvider Provider, RouteHttp Http, TestTime Time, MemoryCache Cache) Build(Func<string, string?> route, DateTime now)
    {
        var http = new RouteHttp(route);
        var time = new TestTime(now);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new YahooMarketDataProvider(http, cache, Options.Create(new MarketDataOptions()), new MarketClock(time), NullLogger<YahooMarketDataProvider>.Instance);
        return (provider, http, time, cache);
    }

    private static PriceTick Tick(string symbol, decimal price, DateTime time, int hours = 1) => new(symbol, price, time, hours);

    [Fact]
    public async Task Many_symbols_take_one_request()
    {
        var (provider, http, _, _) = Build(u => u.Contains("v7/finance/quote") ? Batch(Row("AAA", 10, FridayClose), Row("BBB", 20, FridayClose), Row("CCC", 30, FridayClose)) : null, MondayOpen);

        var quotes = await provider.GetQuotesAsync(["AAA", "BBB", "CCC"]);

        Assert.Equal(3, quotes.Count);
        Assert.Single(http.Requests, r => r.Contains("v7/finance/quote"));
        Assert.DoesNotContain(http.Requests, r => r.Contains("v8/finance/chart"));
    }

    [Fact]
    public async Task If_the_batch_is_refused_quotes_still_come_from_the_chart_endpoint()
    {
        var chart = JsonSerializer.Serialize(new
        {
            chart = new { result = new[] { new { meta = new { currency = "USD", instrumentType = "EQUITY", exchangeTimezoneName = "America/New_York", regularMarketPrice = 42m, regularMarketTime = Unix(FridayClose) } } } },
        });
        var (provider, http, _, _) = Build(u => u.Contains("v8/finance/chart") ? chart : null, MondayOpen);

        var quote = await provider.GetQuoteAsync("AAA");

        Assert.Equal(42m, quote!.Price);
        Assert.Contains(http.Requests, r => r.Contains("v7/finance/quote"));
    }

    [Fact]
    public async Task A_trade_in_the_session_updates_the_price_and_its_time()
    {
        var (provider, _, _, _) = Build(_ => Batch(Row("AAA", 100, MondayOpen.AddSeconds(1))), MondayOpen.AddMinutes(1));
        await provider.GetQuoteAsync("AAA");

        var at = MondayOpen.AddSeconds(30);
        provider.ApplyTick(Tick("AAA", 101.5m, at));

        var updates = provider.TakeLiveUpdates();
        var quote = Assert.Single(updates);
        Assert.Equal(101.5m, quote.Price);
        Assert.Equal(at, quote.PriceTime);
        Assert.Equal(1.5m, quote.Change);
        Assert.Equal(101.5m, quote.DayHigh);
        Assert.Empty(provider.TakeLiveUpdates());
    }

    [Fact]
    public async Task An_out_of_order_trade_is_ignored()
    {
        var (provider, _, _, _) = Build(_ => Batch(Row("AAA", 100, MondayOpen.AddSeconds(1))), MondayOpen.AddMinutes(1));
        await provider.GetQuoteAsync("AAA");
        provider.ApplyTick(Tick("AAA", 101m, MondayOpen.AddSeconds(30)));
        provider.ApplyTick(Tick("AAA", 99m, MondayOpen.AddSeconds(20)));

        Assert.Equal(101m, (await provider.GetQuoteAsync("AAA"))!.Price);
    }

    [Theory]
    [InlineData(9, 0, 0)] // The stream leaves its session field out before the open.
    [InlineData(9, 0, 1)] // Even if it wrongly says regular, the clock decides.
    [InlineData(16, 30, 2)]
    public async Task A_trade_outside_the_session_never_replaces_the_price(int etHour, int etMinute, int hours)
    {
        var (provider, _, _, _) = Build(_ => Batch(Row("AAA", 100, FridayClose)), MondayOpen.AddHours(-1));
        await provider.GetQuoteAsync("AAA");

        var at = new DateTime(2026, 11, 2, etHour + 5, etMinute, 0, DateTimeKind.Utc); // EST is UTC-5
        provider.ApplyTick(Tick("AAA", 120m, at, hours));

        var quote = (await provider.GetQuoteAsync("AAA"))!;
        Assert.Equal(100m, quote.Price);
        Assert.Equal(FridayClose, quote.PriceTime);
        Assert.Equal(120m, quote.ExtendedPrice);
        Assert.Equal(20m, quote.ExtendedChangePercent);
        Assert.Equal(etHour < 10 ? "Pre-market" : "After hours", quote.ExtendedSession);
    }

    [Fact]
    public async Task A_pre_market_trade_can_not_fill_an_order_at_the_open()
    {
        var (provider, _, time, _) = Build(_ => Batch(Row("AAA", 100, FridayClose)), MondayOpen.AddHours(-1));
        await provider.GetQuoteAsync("AAA");
        provider.ApplyTick(Tick("AAA", 120m, MondayOpen.AddMinutes(-5), 0));

        time.Now = MondayOpen.AddSeconds(5);
        var clock = new MarketClock(time);
        Assert.Contains("hasn't traded yet this session", clock.WhyCannotFill((await provider.GetQuoteAsync("AAA"))!));

        provider.ApplyTick(Tick("AAA", 110m, MondayOpen.AddSeconds(3)));
        var quote = (await provider.GetQuoteAsync("AAA"))!;
        Assert.Null(clock.WhyCannotFill(quote));
        Assert.Equal(110m, quote.Price);
        Assert.Null(quote.ExtendedPrice);
    }

    [Fact]
    public async Task A_batch_check_that_lags_the_stream_does_not_move_the_price_backwards()
    {
        var (provider, _, _, cache) = Build(_ => Batch(Row("AAA", 100, MondayOpen.AddSeconds(1))), MondayOpen.AddMinutes(1));
        await provider.GetQuoteAsync("AAA");
        provider.ApplyTick(Tick("AAA", 105m, MondayOpen.AddSeconds(30)));

        cache.Remove("q:AAA");
        var quote = (await provider.GetQuoteAsync("AAA"))!;

        Assert.Equal(105m, quote.Price);
        Assert.Equal(100m, quote.PreviousClose);
    }

    [Fact]
    public async Task After_hours_prices_from_the_batch_are_kept_apart_from_the_price()
    {
        var (provider, _, _, _) = Build(_ => Batch(Row("AAA", 100, FridayClose, post: 101m, postTime: FridayClose.AddHours(1))), MondayOpen.AddHours(-3));

        var quote = (await provider.GetQuoteAsync("AAA"))!;

        Assert.Equal(100m, quote.Price);
        Assert.Equal(101m, quote.ExtendedPrice);
        Assert.Equal(1m, quote.ExtendedChangePercent);
        Assert.Equal("After hours", quote.ExtendedSession);
    }

    [Fact]
    public void A_trade_for_a_symbol_with_no_quote_yet_waits_for_one()
    {
        var (provider, _, _, _) = Build(_ => null, MondayOpen);
        provider.ApplyTick(Tick("AAA", 101m, MondayOpen));
        Assert.Empty(provider.TakeLiveUpdates());
    }

    [Fact]
    public async Task Movers_keep_yahoos_order_and_only_tradable_us_stocks_and_seed_quotes()
    {
        var gainers = new[] { Row("UP1", 50, FridayClose), Row("UP2", 40, FridayClose), Row("CL=F", 70, FridayClose, type: "FUTURE") };
        string Screener(params object[] rows) => JsonSerializer.Serialize(new { finance = new { result = new[] { new { quotes = rows } } } });
        var (provider, http, _, _) = Build(u =>
            u.Contains("scrIds=day_gainers") ? Screener(gainers) :
            u.Contains("scrIds=day_losers") ? Screener(Row("DOWN", 5, FridayClose)) :
            u.Contains("scrIds=most_actives") ? Screener(Row("BUSY", 9, FridayClose)) :
            u.Contains("finance/trending") ? JsonSerializer.Serialize(new { finance = new { result = new[] { new { quotes = new[] { new { symbol = "HOT" }, new { symbol = "0981.HK" }, new { symbol = "CL=F" } } } } } }) :
            u.Contains("v7/finance/quote") ? Batch(Row("HOT", 1, FridayClose), Row("0981.HK", 2, FridayClose, zone: "Asia/Hong_Kong"), Row("CL=F", 3, FridayClose, type: "FUTURE"), Row("^GSPC", 7000, FridayClose, type: "INDEX")) :
            null, MondayOpen);

        var movers = await provider.GetMoversAsync();

        Assert.Equal(["UP1", "UP2"], movers.Gainers.Select(q => q.Symbol));
        Assert.Equal(["DOWN"], movers.Losers.Select(q => q.Symbol));
        Assert.Equal(["BUSY"], movers.MostActive.Select(q => q.Symbol));
        Assert.Equal(["HOT"], movers.Trending.Select(q => q.Symbol));
        Assert.Contains(movers.Indexes, q => q.Symbol == "^GSPC");
        Assert.False(movers.Stale);

        // Watching a mover needs no further request, as the list already had its quote.
        var before = http.Requests.Count;
        await provider.GetQuoteAsync("UP1");
        Assert.Equal(before, http.Requests.Count);

        // Everyone shares one copy for a minute.
        await provider.GetMoversAsync();
        Assert.Equal(before, http.Requests.Count);
    }

    [Fact]
    public async Task With_no_lists_at_all_the_feed_reports_unavailable()
    {
        var (provider, _, _, _) = Build(_ => null, MondayOpen);
        await Assert.ThrowsAsync<MarketDataUnavailableException>(() => provider.GetMoversAsync());
    }

    [Theory]
    [InlineData("^GSPC", true)]
    [InlineData("^^GSPC", false)]
    [InlineData("^", false)]
    [InlineData("A^B", false)]
    public void Index_symbols_are_valid_but_only_with_one_leading_caret(string symbol, bool valid) =>
        Assert.Equal(valid, Symbols.TryNormalize(symbol, out _));
}

public class MoversApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly ApiFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Movers_need_an_account_and_come_back_with_live_fields()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/market/movers")).StatusCode);

        var quote = new Quote("AAA", "Aaa", "USD", "TEST", "EQUITY", 110, 100, null, null, null, null, null, true, DateTime.UtcNow, ExtendedPrice: 111m, ExtendedSession: "After hours");
        _factory.Market.Movers = new([], [], [quote], [], [], DateTime.UtcNow, false);

        var signUp = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("movers@example.com", "correct-horse", "Test"));
        var auth = await signUp.Content.ReadFromJsonAsync<AuthResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth!.Token);

        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/market/movers"));
        var row = body.RootElement.GetProperty("gainers")[0];
        Assert.Equal("AAA", row.GetProperty("symbol").GetString());
        Assert.Equal(10m, row.GetProperty("change").GetDecimal());
        Assert.Equal(111m, row.GetProperty("extendedPrice").GetDecimal());
    }
}
