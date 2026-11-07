using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Contracts;
using PaperTrading.Api.Domain;
using PaperTrading.Api.Insights;
using PaperTrading.Api.MarketData;

namespace PaperTrading.Tests;

public class NyseCalendarTests
{
    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    /// <summary>Copied from https://www.nyse.com/trade/hours-calendars, read 2026-11-02.</summary>
    private static readonly DateOnly[] OfficialHolidays =
    [
        new(2026, 1, 1), new(2026, 1, 19), new(2026, 2, 16), new(2026, 4, 3), new(2026, 5, 25), new(2026, 6, 19), new(2026, 7, 3), new(2026, 9, 7), new(2026, 11, 26), new(2026, 12, 25),
        new(2027, 1, 1), new(2027, 1, 18), new(2027, 2, 15), new(2027, 3, 26), new(2027, 5, 31), new(2027, 6, 18), new(2027, 7, 5), new(2027, 9, 6), new(2027, 11, 25), new(2027, 12, 24),
        new(2028, 1, 17), new(2028, 2, 21), new(2028, 4, 14), new(2028, 5, 29), new(2028, 6, 19), new(2028, 7, 4), new(2028, 9, 4), new(2028, 11, 23), new(2028, 12, 25),
    ];

    private static readonly DateOnly[] OfficialEarlyCloses =
        [new(2026, 11, 27), new(2026, 12, 24), new(2027, 11, 26), new(2028, 7, 3), new(2028, 11, 24)];

    private static IEnumerable<DateOnly> Weekdays2026To2028()
    {
        for (var d = new DateOnly(2026, 1, 1); d.Year <= 2028; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) yield return d;
    }

    [Fact]
    public void Every_weekday_from_2026_to_2028_matches_the_published_holidays()
    {
        Assert.Equal(OfficialHolidays, Weekdays2026To2028().Where(d => !NyseCalendar.IsTradingDay(d)));
    }

    [Fact]
    public void Every_weekday_from_2026_to_2028_matches_the_published_early_closes()
    {
        Assert.Equal(OfficialEarlyCloses, Weekdays2026To2028().Where(NyseCalendar.IsEarlyClose));
    }

    [Theory] // Past dates matter too, because scoring goes back through a channel's old videos.
    [InlineData(2024, 11, 29)]
    [InlineData(2024, 12, 24)]
    [InlineData(2025, 7, 3)]
    public void Past_early_closes_are_known(int y, int m, int d) => Assert.True(NyseCalendar.IsEarlyClose(new DateOnly(y, m, d)));

    [Fact]
    public void Saturday_new_years_day_keeps_the_previous_friday_open()
    {
        Assert.True(NyseCalendar.IsTradingDay(new DateOnly(2027, 12, 31)));
        Assert.Equal(MarketPhase.Weekend, NyseCalendar.StatusAt(Utc(2028, 1, 1, 15)).Phase);
    }

    [Theory] // Daylight saving ended on Nov 1, 2026, so the same 9:30 and 4:00 fall an hour later in UTC.
    [InlineData(2026, 10, 30, 13, 29, 59, MarketPhase.PreMarket)]
    [InlineData(2026, 10, 30, 13, 30, 0, MarketPhase.Open)]
    [InlineData(2026, 10, 30, 19, 59, 59, MarketPhase.Open)]
    [InlineData(2026, 10, 30, 20, 0, 0, MarketPhase.AfterHours)]
    [InlineData(2026, 11, 2, 14, 29, 59, MarketPhase.PreMarket)]
    [InlineData(2026, 11, 2, 14, 30, 0, MarketPhase.Open)]
    [InlineData(2026, 11, 2, 20, 59, 59, MarketPhase.Open)]
    [InlineData(2026, 11, 2, 21, 0, 0, MarketPhase.AfterHours)]
    [InlineData(2026, 11, 3, 1, 0, 0, MarketPhase.Overnight)] // 8 PM ET
    [InlineData(2026, 10, 31, 15, 0, 0, MarketPhase.Weekend)]
    [InlineData(2026, 11, 1, 15, 0, 0, MarketPhase.Weekend)]
    [InlineData(2026, 11, 26, 15, 0, 0, MarketPhase.Holiday)]
    [InlineData(2026, 11, 27, 17, 59, 59, MarketPhase.Open)] // 12:59:59 PM ET on an early close
    [InlineData(2026, 11, 27, 18, 0, 0, MarketPhase.AfterHours)]
    public void The_phase_follows_new_york_time(int y, int mo, int d, int h, int mi, int s, MarketPhase expected) =>
        Assert.Equal(expected, NyseCalendar.StatusAt(Utc(y, mo, d, h, mi, s)).Phase);

    [Fact]
    public void A_friday_evening_in_new_york_is_still_friday_even_though_utc_has_moved_on()
    {
        Assert.Equal(new DateOnly(2026, 10, 30), NyseCalendar.DateInNewYork(Utc(2026, 10, 31, 1)));
    }

    [Fact]
    public void Status_says_when_trading_resumes_in_eastern_time()
    {
        var saturday = NyseCalendar.StatusAt(Utc(2026, 10, 31, 15));
        Assert.Equal("Closed for the weekend", saturday.Label);
        Assert.Equal("Opens Mon, Nov 2 at 9:30 AM ET", saturday.Detail);
        Assert.Equal(Utc(2026, 11, 2, 14, 30), saturday.NextOpen);

        var thanksgiving = NyseCalendar.StatusAt(Utc(2026, 11, 26, 15));
        Assert.Equal("Closed for Thanksgiving Day", thanksgiving.Label);
        Assert.Equal("Opens Fri, Nov 27 at 9:30 AM ET", thanksgiving.Detail);

        Assert.Equal("Closes at 1:00 PM ET", NyseCalendar.StatusAt(Utc(2026, 11, 27, 15)).Detail);
        Assert.Equal("Opens at 9:30 AM ET", NyseCalendar.StatusAt(Utc(2026, 11, 2, 10)).Detail);
    }

    [Fact]
    public void Session_start_is_the_last_open_and_never_the_next_one()
    {
        Assert.Equal(Utc(2026, 10, 30, 13, 30), NyseCalendar.StatusAt(Utc(2026, 10, 31, 15)).SessionStart);
        Assert.Equal(Utc(2026, 11, 25, 14, 30), NyseCalendar.StatusAt(Utc(2026, 11, 26, 15)).SessionStart);
        Assert.Equal(Utc(2026, 11, 2, 14, 30), NyseCalendar.StatusAt(Utc(2026, 11, 2, 14, 30)).SessionStart);
    }
}

public class OrderGateTests
{
    private static readonly DateTime FridayClosePrint = new(2026, 10, 30, 20, 0, 1, DateTimeKind.Utc);
    private static readonly DateTime Saturday = new(2026, 10, 31, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MondayOpen = new(2026, 11, 2, 14, 30, 0, DateTimeKind.Utc);

    private static PlaceOrderRequest Market(OrderSide side, decimal qty) => new("AAA", side, OrderType.Market, qty, null, null);
    private static PlaceOrderRequest Limit(OrderSide side, decimal qty, decimal price) => new("AAA", side, OrderType.Limit, qty, price, null);
    private static PlaceOrderRequest Stop(OrderSide side, decimal qty, decimal price) => new("AAA", side, OrderType.Stop, qty, null, price);

    private static (Harness H, TestTime Time) Closed()
    {
        var time = new TestTime(Saturday);
        var h = new Harness(clock: new MarketClock(time));
        h.Market.Set("AAA", 100m);
        h.Market.PriceTime = FridayClosePrint;
        return (h, time);
    }

    private static async Task Tick(Harness h) => await h.Orders.MatchOpenOrdersAsync(await h.Market.GetQuotesAsync(["AAA"]));

    [Theory]
    [InlineData(OrderSide.Buy)]
    [InlineData(OrderSide.Sell)]
    public async Task A_market_order_is_refused_at_the_weekend_and_nothing_moves(OrderSide side)
    {
        var (h, _) = Closed();
        using (h)
        {
            var ex = await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Market(side, 1)));

            Assert.Equal(409, ex.StatusCode);
            Assert.Contains("Opens Mon, Nov 2 at 9:30 AM ET", ex.Message);
            Assert.Equal(0, await h.Db.Orders.CountAsync());
            Assert.Equal(10_000m, (await h.Db.Users.SingleAsync()).Cash);
        }
    }

    [Theory]
    [InlineData(2026, 10, 30, 21, 0)] // After hours
    [InlineData(2026, 11, 2, 12, 0)] // Pre-market
    [InlineData(2026, 11, 26, 16, 0)] // Thanksgiving, in the middle of what's normally the trading day
    [InlineData(2026, 11, 27, 18, 30)] // 1:30 PM ET on an early close
    public async Task A_market_order_is_refused_whenever_the_regular_session_is_shut(int y, int mo, int d, int h24, int mi)
    {
        var (h, time) = Closed();
        using (h)
        {
            time.Now = new DateTime(y, mo, d, h24, mi, 0, DateTimeKind.Utc);
            await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 1)));
        }
    }

    [Fact]
    public async Task A_limit_that_could_already_fill_waits_while_the_market_is_closed()
    {
        var (h, _) = Closed();
        using (h)
        {
            var order = await h.Orders.PlaceAsync(h.UserId, Limit(OrderSide.Buy, 1, 105m));
            await Tick(h);

            Assert.Equal(OrderStatus.Open, order.Status);
            Assert.Equal(0, await h.Db.Fills.CountAsync());
            Assert.Equal(10_000m, (await h.Db.Users.SingleAsync()).Cash);
        }
    }

    [Fact]
    public async Task A_resting_order_ignores_friday_prices_at_the_bell_then_fills_on_the_first_real_trade()
    {
        var (h, time) = Closed();
        using (h)
        {
            var order = await h.Orders.PlaceAsync(h.UserId, Limit(OrderSide.Buy, 1, 105m));

            // The bell has rung, but the feed still shows Friday's close.
            time.Now = MondayOpen.AddSeconds(5);
            await Tick(h);
            Assert.Equal(OrderStatus.Open, order.Status);

            // The stock opens down at 90, so that's the price, rather than Friday's 100.
            h.Market.Set("AAA", 90m);
            h.Market.PriceTime = MondayOpen.AddSeconds(8);
            time.Now = MondayOpen.AddSeconds(10);
            await Tick(h);

            Assert.Equal(OrderStatus.Filled, order.Status);
            Assert.Equal(90m, order.FilledPrice);
        }
    }

    [Fact]
    public async Task A_market_order_just_after_the_bell_waits_for_the_first_trade()
    {
        var (h, time) = Closed();
        using (h)
        {
            time.Now = MondayOpen.AddSeconds(5);
            var ex = await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 1)));
            Assert.Contains("hasn't traded yet this session", ex.Message);

            h.Market.PriceTime = MondayOpen.AddSeconds(8);
            time.Now = MondayOpen.AddSeconds(10);
            var order = await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 1));
            Assert.Equal(OrderStatus.Filled, order.Status);
        }
    }

    [Fact]
    public async Task A_stop_placed_at_the_weekend_triggers_only_once_the_market_is_open()
    {
        var (h, time) = Closed();
        using (h)
        {
            var order = await h.Orders.PlaceAsync(h.UserId, Stop(OrderSide.Buy, 1, 110m));

            // Prices can't really move at the weekend, but a bad feed must not be able to fill anything either.
            h.Market.Set("AAA", 120m);
            await Tick(h);
            Assert.Equal(OrderStatus.Open, order.Status);

            h.Market.PriceTime = MondayOpen.AddSeconds(1);
            time.Now = MondayOpen.AddSeconds(2);
            await Tick(h);
            Assert.Equal(OrderStatus.Filled, order.Status);
            Assert.Equal(120m, order.FilledPrice);
        }
    }

    [Fact]
    public async Task An_open_order_can_be_cancelled_while_the_market_is_closed()
    {
        var (h, _) = Closed();
        using (h)
        {
            var order = await h.Orders.PlaceAsync(h.UserId, Limit(OrderSide.Buy, 1, 90m));
            await h.Orders.CancelAsync(h.UserId, order.Id);
            Assert.Equal(OrderStatus.Cancelled, order.Status);
        }
    }

    [Fact]
    public async Task A_stock_with_no_recent_trade_does_not_fill_on_its_old_price()
    {
        var (h, time) = Closed();
        using (h)
        {
            h.Market.PriceTime = MondayOpen.AddMinutes(1);
            time.Now = MondayOpen.AddMinutes(45);
            var ex = await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 1)));
            Assert.Contains("hasn't traded for over 30 minutes", ex.Message);
        }
    }

    [Fact]
    public async Task A_day_the_exchange_unexpectedly_stays_shut_fills_nothing()
    {
        var (h, time) = Closed();
        using (h)
        {
            // A weekday the calendar calls normal, but with no trades since Friday.
            time.Now = MondayOpen.AddHours(2);
            await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 1)));
        }
    }
}

public class MarketHoursApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly ApiFactory _factory = new()
    {
        Clock = new MarketClock(new TestTime(new DateTime(2026, 10, 31, 15, 0, 0, DateTimeKind.Utc))),
    };

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_api_refuses_a_weekend_market_order_and_keeps_a_limit_order()
    {
        _factory.Market.Set("AAA", 100m);
        _factory.Market.PriceTime = new DateTime(2026, 10, 30, 20, 0, 1, DateTimeKind.Utc);

        var client = _factory.CreateClient();
        var signUp = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("hours@example.com", "correct-horse", "Test"));
        var auth = await signUp.Content.ReadFromJsonAsync<AuthResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth!.Token);

        var status = await client.GetFromJsonAsync<MarketStatus>("/api/market/status", Json);
        Assert.Equal(MarketPhase.Weekend, status!.Phase);

        var market = await client.PostAsJsonAsync("/api/orders", new PlaceOrderRequest("AAA", OrderSide.Buy, OrderType.Market, 1, null, null), Json);
        Assert.Equal(HttpStatusCode.Conflict, market.StatusCode);
        Assert.Contains("Opens Mon, Nov 2 at 9:30 AM ET", await market.Content.ReadAsStringAsync());

        var limit = await client.PostAsJsonAsync("/api/orders", new PlaceOrderRequest("AAA", OrderSide.Buy, OrderType.Limit, 1, 105m, null), Json);
        Assert.Equal(HttpStatusCode.Created, limit.StatusCode);
        Assert.Equal(OrderStatus.Open, (await limit.Content.ReadFromJsonAsync<OrderDto>(Json))!.Status);
    }
}

/// <summary>Yahoo's real response shape, read back through the real parsing code.</summary>
public class YahooSessionTests
{
    private sealed class StubHttp(string body) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://example.test/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static long Unix(int y, int mo, int d, int h, int mi = 0) => new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static string QuoteJson(string zone, long start, long end, long traded) => JsonSerializer.Serialize(new
    {
        chart = new
        {
            result = new[]
            {
                new
                {
                    meta = new
                    {
                        currency = "USD", exchangeName = "NMS", instrumentType = "EQUITY", exchangeTimezoneName = zone,
                        regularMarketTime = traded, regularMarketPrice = 344.63m, chartPreviousClose = 348.91m,
                        currentTradingPeriod = new { regular = new { timezone = "X", start, end, gmtoffset = 0 } },
                    },
                },
            },
        },
    });

    private static YahooMarketDataProvider Provider(string json, DateTime now) => new(
        new StubHttp(json), new MemoryCache(new MemoryCacheOptions()), Options.Create(new MarketDataOptions()),
        new MarketClock(new TestTime(now)), NullLogger<YahooMarketDataProvider>.Instance);

    [Fact]
    public async Task At_the_weekend_a_us_quote_is_closed_and_its_session_is_friday_rather_than_yahoos_next_session()
    {
        var saturday = new DateTime(2026, 10, 31, 15, 0, 0, DateTimeKind.Utc);
        var json = QuoteJson("America/New_York", Unix(2026, 11, 2, 14, 30), Unix(2026, 11, 2, 21), Unix(2026, 10, 30, 20));

        var quote = await Provider(json, saturday).GetQuoteAsync("AAPL");

        Assert.False(quote!.IsMarketOpen);
        Assert.Equal(new DateTime(2026, 10, 30, 13, 30, 0, DateTimeKind.Utc), quote.SessionStart);
        Assert.Equal(new DateTime(2026, 10, 30, 20, 0, 0, DateTimeKind.Utc), quote.PriceTime);
        Assert.Equal(MarketPhase.Weekend, quote.Market!.Phase);
        Assert.Null(quote.Session);
    }

    [Fact]
    public async Task A_us_holiday_is_closed_even_if_yahoo_reports_that_day_as_a_session()
    {
        var thanksgiving = new DateTime(2026, 11, 26, 16, 0, 0, DateTimeKind.Utc);
        var json = QuoteJson("America/New_York", Unix(2026, 11, 26, 14, 30), Unix(2026, 11, 26, 21), Unix(2026, 11, 25, 21));

        var quote = await Provider(json, thanksgiving).GetQuoteAsync("AAPL");

        Assert.False(quote!.IsMarketOpen);
        Assert.Equal("Closed for Thanksgiving Day", quote.Market!.Label);
    }

    [Fact]
    public async Task An_exchange_outside_new_york_follows_its_own_session()
    {
        // NSE is open during the night in New York.
        var now = new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc);
        var clock = new MarketClock(new TestTime(now));
        var json = QuoteJson("Asia/Kolkata", Unix(2026, 11, 2, 3, 45), Unix(2026, 11, 2, 10), Unix(2026, 11, 2, 4, 59));

        var quote = await Provider(json, now).GetQuoteAsync("RELIANCE.NS");

        Assert.True(quote!.IsMarketOpen);
        Assert.Null(clock.WhyCannotFill(quote));

        var after = new MarketClock(new TestTime(new DateTime(2026, 11, 2, 11, 0, 0, DateTimeKind.Utc)));
        Assert.Contains("closed", after.WhyCannotFill(quote), StringComparison.OrdinalIgnoreCase);
    }

    private static string HistoryJson(string zone, long end, params (long Time, decimal Close)[] bars) => JsonSerializer.Serialize(new
    {
        chart = new
        {
            result = new[]
            {
                new
                {
                    meta = new { exchangeTimezoneName = zone, currentTradingPeriod = new { regular = new { end } } },
                    timestamp = bars.Select(b => b.Time).ToArray(),
                    indicators = new
                    {
                        quote = new[]
                        {
                            new
                            {
                                high = bars.Select(b => b.Close).ToArray(),
                                low = bars.Select(b => b.Close).ToArray(),
                                close = bars.Select(b => b.Close).ToArray(),
                            },
                        },
                    },
                },
            },
        },
    });

    [Fact]
    public async Task Each_bar_closes_at_that_days_new_york_time_whatever_the_season_is_today()
    {
        // Fetched in winter, when today's close is 21:00 UTC.
        var json = HistoryJson("America/New_York", Unix(2026, 11, 2, 21),
            (Unix(2026, 8, 20, 13, 30), 100m), (Unix(2026, 11, 27, 14, 30), 110m), (Unix(2026, 12, 1, 14, 30), 120m));

        var bars = await new YahooPriceHistory(new StubHttp(json)).GetDailyAsync("AAA", new DateOnly(2026, 8, 1));

        Assert.Equal(new DateOnly(2026, 8, 20), bars[0].Date);
        Assert.Equal(new DateTime(2026, 8, 20, 20, 0, 0, DateTimeKind.Utc), bars[0].ClosesAt);
        Assert.Equal(new DateTime(2026, 11, 27, 18, 0, 0, DateTimeKind.Utc), bars[1].ClosesAt); // Early close
        Assert.Equal(new DateTime(2026, 12, 1, 21, 0, 0, DateTimeKind.Utc), bars[2].ClosesAt);
    }

    [Fact]
    public async Task An_indian_bar_closes_at_the_indian_close()
    {
        var json = HistoryJson("Asia/Kolkata", Unix(2026, 11, 2, 10), (Unix(2026, 8, 20, 3, 45), 100m));

        var bars = await new YahooPriceHistory(new StubHttp(json)).GetDailyAsync("RELIANCE.NS", new DateOnly(2026, 8, 1));

        Assert.Equal(new DateOnly(2026, 8, 20), bars[0].Date);
        Assert.Equal(new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc), bars[0].ClosesAt);
    }

    [Fact]
    public async Task A_summer_video_published_after_the_close_gets_the_next_days_close()
    {
        var json = HistoryJson("America/New_York", Unix(2026, 11, 2, 21),
            (Unix(2026, 8, 20, 13, 30), 100m), (Unix(2026, 8, 21, 13, 30), 120m), (Unix(2026, 8, 24, 13, 30), 130m));
        var bars = await new YahooPriceHistory(new StubHttp(json)).GetDailyAsync("AAA", new DateOnly(2026, 8, 1));

        // 4:30 PM ET, when the day's close had already happened. It used to count as 20:30 being before a 21:00 close.
        var madeAt = new DateTime(2026, 8, 20, 20, 30, 0, DateTimeKind.Utc);
        var score = ClaimScoring.Score(ClaimDirection.Bullish, null, madeAt, madeAt.AddDays(3), bars, bars, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(120m, score.EntryPrice);
    }

    [Fact]
    public void A_resolve_time_in_the_evening_uses_the_new_york_date()
    {
        var at = (int d) => new DateTime(2026, 8, d, 20, 0, 0, DateTimeKind.Utc);
        List<DailyBar> bars = [new(new DateOnly(2026, 8, 3), 100, 100, 100, at(3)), new(new DateOnly(2026, 8, 4), 110, 110, 110, at(4)), new(new DateOnly(2026, 8, 5), 120, 120, 120, at(5))];

        // 9 PM ET on the 4th is already the 5th in UTC, but it's still the 4th's trading day.
        var score = ClaimScoring.Score(ClaimDirection.Bullish, null, new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 5, 1, 0, 0, DateTimeKind.Utc), bars, bars, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(110m, score.ExitPrice);
    }
}
