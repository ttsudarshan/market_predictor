using Microsoft.EntityFrameworkCore;
using PaperTrading.Api.Contracts;
using PaperTrading.Api.Domain;
using PaperTrading.Api.MarketData;
using PaperTrading.Api.Services;

namespace PaperTrading.Tests;

public class OrderServiceTests
{
    private static PlaceOrderRequest Market(OrderSide side, decimal qty, string symbol = "AAA") =>
        new(symbol, side, OrderType.Market, qty, null, null);
    private static PlaceOrderRequest Limit(OrderSide side, decimal qty, decimal price, string symbol = "AAA") =>
        new(symbol, side, OrderType.Limit, qty, price, null);
    private static PlaceOrderRequest Stop(OrderSide side, decimal qty, decimal price, string symbol = "AAA") =>
        new(symbol, side, OrderType.Stop, qty, null, price);

    private static async Task<IReadOnlyList<Order>> Tick(Harness h, params string[] symbols) =>
        await h.Orders.MatchOpenOrdersAsync(await h.Market.GetQuotesAsync(symbols));

    [Fact]
    public async Task Market_buy_moves_cash_into_a_position_and_writes_a_fill()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);

        var order = await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(100m, order.FilledPrice);
        var p = await h.Portfolio.GetAsync(h.UserId);
        Assert.Equal(9_000m, p.Cash);
        Assert.Equal(10_000m, p.Equity);
        Assert.Equal(10m, Assert.Single(p.Positions).Quantity);
        Assert.Equal(1, await h.Db.Fills.CountAsync());
    }

    [Fact]
    public async Task Buying_more_averages_the_cost_and_selling_realizes_against_it()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10));
        h.Market.Set("AAA", 200m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10));

        Assert.Equal(150m, (await h.Db.Positions.SingleAsync()).AverageCost);

        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Sell, 5));

        var sell = await h.Db.Fills.Where(f => f.Side == OrderSide.Sell).SingleAsync();
        Assert.Equal(250m, sell.RealizedPnl);
        var p = await h.Portfolio.GetAsync(h.UserId);
        Assert.Equal(15m, p.Positions[0].Quantity);
        Assert.Equal(750m, p.UnrealizedPnl);
        Assert.Equal(250m, p.RealizedPnl);
    }

    [Fact]
    public async Task Selling_everything_removes_the_position()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10));
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Sell, 10));

        Assert.Empty((await h.Portfolio.GetAsync(h.UserId)).Positions);
    }

    [Fact]
    public async Task Realized_plus_unrealized_always_equals_total_return_including_commissions()
    {
        using var h = new Harness(cash: 50_000m, commission: 1.5m);
        h.Market.Set("AAA", 33.33m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 7.3m));
        h.Market.Set("AAA", 41.17m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 3.1234m));
        h.Market.Set("AAA", 39.01m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Sell, 2.5m));
        h.Market.Set("AAA", 52.9m);

        var p = await h.Portfolio.GetAsync(h.UserId);

        Assert.Equal(4.5m, p.TotalCommissions);
        Assert.Equal(p.TotalReturn, p.RealizedPnl + p.UnrealizedPnl, precision: 4);
    }

    [Fact]
    public async Task Cannot_buy_more_than_cash_allows_including_commission()
    {
        using var h = new Harness(cash: 1_000m, commission: 1m);
        h.Market.Set("AAA", 100m);

        var ex = await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10)));

        Assert.Contains("buying power", ex.Message);
        Assert.Equal(1_000m, (await h.Portfolio.GetAsync(h.UserId)).Cash);
        Assert.Empty(h.Db.Orders);
    }

    [Fact]
    public async Task Open_buy_orders_reserve_cash_against_new_orders()
    {
        using var h = new Harness(cash: 1_000m);
        h.Market.Set("AAA", 100m);
        await h.Orders.PlaceAsync(h.UserId, Limit(OrderSide.Buy, 8, 90m));

        var p = await h.Portfolio.GetAsync(h.UserId);
        Assert.Equal(720m, p.ReservedCash);
        Assert.Equal(280m, p.BuyingPower);
        await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 3)));
    }

    [Fact]
    public async Task Cannot_short_or_sell_shares_held_by_an_open_sell_order()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Sell, 1)));

        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10));
        await h.Orders.PlaceAsync(h.UserId, Limit(OrderSide.Sell, 8, 150m));

        await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Sell, 3)));
        Assert.Equal(OrderStatus.Filled, (await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Sell, 2))).Status);
    }

    [Fact]
    public async Task Limit_buy_rests_then_fills_at_last_price_when_price_reaches_it()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        var order = await h.Orders.PlaceAsync(h.UserId, Limit(OrderSide.Buy, 10, 90m));
        Assert.Equal(OrderStatus.Open, order.Status);

        h.Market.Set("AAA", 90.01m);
        Assert.Empty(await Tick(h, "AAA"));

        h.Market.Set("AAA", 85m);
        var changed = Assert.Single(await Tick(h, "AAA"));

        Assert.Equal(OrderStatus.Filled, changed.Status);
        Assert.Equal(85m, changed.FilledPrice);
        Assert.Equal(9_150m, (await h.Portfolio.GetAsync(h.UserId)).Cash);
    }

    [Fact]
    public async Task Marketable_limit_fills_immediately_at_the_better_last_price()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);

        var order = await h.Orders.PlaceAsync(h.UserId, Limit(OrderSide.Buy, 1, 120m));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(100m, order.FilledPrice);
    }

    [Theory]
    [InlineData(OrderSide.Sell, 95, 95.01, 94.99)]
    [InlineData(OrderSide.Buy, 105, 104.99, 105.5)]
    public async Task Stop_order_triggers_only_once_price_crosses(OrderSide side, decimal stop, decimal before, decimal after)
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        if (side == OrderSide.Sell) await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10));

        var order = await h.Orders.PlaceAsync(h.UserId, Stop(side, 10, stop));
        Assert.Equal(OrderStatus.Open, order.Status);

        h.Market.Set("AAA", before);
        Assert.Empty(await Tick(h, "AAA"));

        h.Market.Set("AAA", after);
        var changed = Assert.Single(await Tick(h, "AAA"));
        Assert.Equal(OrderStatus.Filled, changed.Status);
        Assert.Equal(after, changed.FilledPrice);
    }

    [Theory]
    [InlineData(OrderSide.Buy, 99)]
    [InlineData(OrderSide.Sell, 101)]
    public async Task Stop_already_through_the_market_is_refused(OrderSide side, decimal stop)
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 1));

        await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Stop(side, 1, stop)));
    }

    [Fact]
    public async Task Buy_stop_gapping_past_available_cash_is_rejected_without_touching_the_balance()
    {
        using var h = new Harness(cash: 1_050m);
        h.Market.Set("AAA", 100m);
        await h.Orders.PlaceAsync(h.UserId, Stop(OrderSide.Buy, 10, 105m));

        h.Market.Set("AAA", 200m);
        var changed = Assert.Single(await Tick(h, "AAA"));

        Assert.Equal(OrderStatus.Rejected, changed.Status);
        Assert.Contains("Not enough cash", changed.RejectReason);
        Assert.Equal(1_050m, (await h.Portfolio.GetAsync(h.UserId)).Cash);
        Assert.Empty(h.Db.Fills);
    }

    [Fact]
    public async Task Cancelled_order_never_fills_and_releases_reserved_cash()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        var order = await h.Orders.PlaceAsync(h.UserId, Limit(OrderSide.Buy, 10, 90m));

        await h.Orders.CancelAsync(h.UserId, order.Id);

        h.Market.Set("AAA", 50m);
        Assert.Empty(await Tick(h, "AAA"));
        Assert.Equal(0m, (await h.Portfolio.GetAsync(h.UserId)).ReservedCash);
        var again = await Assert.ThrowsAsync<TradingException>(() => h.Orders.CancelAsync(h.UserId, order.Id));
        Assert.Equal(409, again.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1.23456)]
    public async Task Bad_quantities_are_refused(decimal qty)
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, qty)));
    }

    [Fact]
    public async Task Fractional_shares_work()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 0.5m));
        Assert.Equal(9_950m, (await h.Portfolio.GetAsync(h.UserId)).Cash);
    }

    [Theory]
    [InlineData("INR", "EQUITY", "trades in INR")]
    [InlineData("USD", "INDEX", "not a stock or ETF")]
    public async Task Foreign_currency_and_non_equity_symbols_are_refused(string currency, string type, string expected)
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m, currency, type);
        var ex = await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 1)));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Unknown_symbol_is_refused_and_a_dead_feed_does_not_execute()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<TradingException>(() => h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 1, "NOPE")));

        h.Market.Failing = true;
        await Assert.ThrowsAsync<MarketDataUnavailableException>(() => h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 1)));
        Assert.Empty(h.Db.Orders);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("AAA?x=1")]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAA")]
    public void Symbols_that_could_alter_the_upstream_url_are_refused(string symbol) =>
        Assert.Throws<TradingException>(() => Symbols.Normalize(symbol));

    [Fact]
    public async Task Concurrent_orders_cannot_overspend()
    {
        // Each order gets its own context, like concurrent requests, and they share one gate.
        var path = Path.Combine(Path.GetTempPath(), $"pt-{Guid.NewGuid()}.db");
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<PaperTrading.Api.Data.AppDbContext>()
            .UseSqlite($"Data Source={path}").Options;
        try
        {
            Guid userId;
            await using (var setup = new PaperTrading.Api.Data.AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var user = new User { Email = "c@example.com", DisplayName = "C", Cash = 1_000m, StartingBalance = 1_000m };
                setup.Users.Add(user);
                await setup.SaveChangesAsync();
                userId = user.Id;
            }

            var market = new FakeMarket();
            market.Set("AAA", 100m);
            var gate = new ExecutionGate();
            var trading = Microsoft.Extensions.Options.Options.Create(new TradingOptions());

            var attempts = Enumerable.Range(0, 25).Select(async _ =>
            {
                await using var db = new PaperTrading.Api.Data.AppDbContext(options);
                try
                {
                    await new OrderService(db, market, new AlwaysOpenClock(), gate, trading).PlaceAsync(userId, Market(OrderSide.Buy, 1));
                    return true;
                }
                catch (TradingException) { return false; }
            });
            var results = await Task.WhenAll(attempts);

            Assert.Equal(10, results.Count(r => r));
            await using var check = new PaperTrading.Api.Data.AppDbContext(options);
            Assert.Equal(0m, (await check.Users.SingleAsync()).Cash);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Reset_wipes_history_and_keeps_the_watchlist()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        h.Db.WatchLists.Add(new WatchList { Id = h.UserId, UserId = h.UserId, Name = "Favorites" });
        h.Db.WatchlistItems.Add(new WatchlistItem { WatchListId = h.UserId, Symbol = "AAA" });
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10));
        await h.Orders.PlaceAsync(h.UserId, Limit(OrderSide.Buy, 1, 50m));

        await h.Accounts.ResetAsync(h.UserId, 25_000m);
        h.Db.ChangeTracker.Clear();

        var p = await h.Portfolio.GetAsync(h.UserId);
        Assert.Equal(25_000m, p.Cash);
        Assert.Equal(25_000m, p.StartingBalance);
        Assert.Empty(p.Positions);
        Assert.Empty(h.Db.Orders);
        Assert.Empty(h.Db.Fills);
        Assert.Single(h.Db.WatchlistItems);
    }

    [Fact]
    public async Task Todays_change_starts_from_the_fill_for_shares_bought_this_session()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m, previousClose: 110m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10));

        Assert.Equal(0m, (await h.Portfolio.GetAsync(h.UserId)).DayChange);

        h.Market.Set("AAA", 105m, previousClose: 110m);
        Assert.Equal(50m, (await h.Portfolio.GetAsync(h.UserId)).DayChange);
    }

    [Fact]
    public async Task Todays_change_starts_from_previous_close_for_shares_held_overnight()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10));

        h.Market.SessionStart = DateTime.UtcNow.AddMinutes(1);
        h.Market.Set("AAA", 105m, previousClose: 110m);

        Assert.Equal(-50m, (await h.Portfolio.GetAsync(h.UserId)).DayChange);
    }

    [Fact]
    public async Task Todays_change_blends_overnight_shares_with_shares_bought_this_session()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10));
        h.Market.SessionStart = DateTime.UtcNow;
        await Task.Delay(5);

        h.Market.Set("AAA", 120m, previousClose: 110m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10));
        h.Market.Set("AAA", 130m, previousClose: 110m);

        // 10 shares up 20 from the close, plus 10 shares up 10 from the fill.
        Assert.Equal(300m, (await h.Portfolio.GetAsync(h.UserId)).DayChange);
    }

    [Fact]
    public async Task Portfolio_history_ends_with_current_equity_and_snapshot_skips_unchanged_values()
    {
        using var h = new Harness();
        h.Market.Set("AAA", 100m);
        await h.Orders.PlaceAsync(h.UserId, Market(OrderSide.Buy, 10));
        await h.Portfolio.RecordSnapshotAsync(h.UserId);
        await h.Portfolio.RecordSnapshotAsync(h.UserId);
        Assert.Equal(1, await h.Db.Snapshots.CountAsync());

        h.Market.Set("AAA", 110m);
        var history = await h.Portfolio.GetHistoryAsync(h.UserId);

        Assert.Equal(2, history.Count);
        Assert.Equal(10_100m, history[^1].Equity);
    }
}
