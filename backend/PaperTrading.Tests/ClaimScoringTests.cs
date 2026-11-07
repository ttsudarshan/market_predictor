using PaperTrading.Api.Domain;
using PaperTrading.Api.Insights;
using PaperTrading.Api.MarketData;
using static PaperTrading.Tests.Bars;

namespace PaperTrading.Tests;

public class ClaimScoringTests
{
    // Monday 5 January to Friday 16 January 2026, weekends left out as on a real exchange.
    private static readonly List<DailyBar> Stock =
    [
        On("2026-01-05", 100), On("2026-01-06", 102), On("2026-01-07", 104), On("2026-01-08", 103, high: 111),
        On("2026-01-09", 106), On("2026-01-12", 108), On("2026-01-13", 110), On("2026-01-14", 112, low: 95),
        On("2026-01-15", 114), On("2026-01-16", 120),
    ];

    private static readonly List<DailyBar> Spy =
    [
        On("2026-01-05", 500), On("2026-01-06", 500), On("2026-01-07", 505), On("2026-01-08", 505),
        On("2026-01-09", 505), On("2026-01-12", 510), On("2026-01-13", 510), On("2026-01-14", 510),
        On("2026-01-15", 515), On("2026-01-16", 515),
    ];

    private static ScoreResult Score(ClaimDirection direction, string madeAt, string resolvesAt, decimal? target = null, string now = "2026-01-20T00:00:00") =>
        ClaimScoring.Score(direction, target, Utc(madeAt), Utc(resolvesAt), Stock, Spy, Utc(now));

    [Fact]
    public void Bullish_call_scores_return_and_excess_over_the_benchmark()
    {
        var r = Score(ClaimDirection.Bullish, "2026-01-06T12:00:00", "2026-01-13T12:00:00");

        Assert.Equal(ClaimStatus.Final, r.Status);
        Assert.Equal(102m, r.EntryPrice);                 // first close at or after publication
        Assert.Equal(110m, r.ExitPrice);
        Assert.Equal(7.8431m, r.ReturnPercent);           // 102 to 110
        Assert.Equal(5.8431m, r.ExcessReturnPercent);     // less SPY's 500 to 510, which is 2%
    }

    [Fact]
    public void Bearish_call_that_was_wrong_scores_negative_on_both_measures()
    {
        var r = Score(ClaimDirection.Bearish, "2026-01-06T12:00:00", "2026-01-13T12:00:00");

        Assert.Equal(-7.8431m, r.ReturnPercent);
        Assert.Equal(-5.8431m, r.ExcessReturnPercent);
    }

    [Fact]
    public void Bearish_call_is_right_when_the_price_falls_and_beats_the_benchmark_by_falling_more()
    {
        var falling = new List<DailyBar> { On("2026-01-05", 100), On("2026-01-06", 90) };
        var flat = new List<DailyBar> { On("2026-01-05", 500), On("2026-01-06", 505) };

        var r = ClaimScoring.Score(ClaimDirection.Bearish, null, Utc("2026-01-05T12:00:00"), Utc("2026-01-06T12:00:00"), falling, flat, Utc("2026-01-07T00:00:00"));

        Assert.Equal(10m, r.ReturnPercent);
        Assert.Equal(11m, r.ExcessReturnPercent);
    }

    [Fact]
    public void Video_published_after_the_close_enters_on_the_next_close_so_there_is_no_look_ahead()
    {
        // 22:00 UTC on Tuesday is after that day's 21:00 close, so Wednesday's close is the first viewers could get.
        var r = Score(ClaimDirection.Bullish, "2026-01-06T22:00:00", "2026-01-13T12:00:00");

        Assert.Equal(104m, r.EntryPrice);
    }

    [Fact]
    public void Video_published_at_the_weekend_enters_on_monday()
    {
        var r = Score(ClaimDirection.Bullish, "2026-01-10T15:00:00", "2026-01-14T12:00:00");

        Assert.Equal(108m, r.EntryPrice);
        Assert.Equal(112m, r.ExitPrice);
    }

    [Fact]
    public void A_resolve_date_on_a_weekend_uses_the_next_trading_day()
    {
        var r = Score(ClaimDirection.Bullish, "2026-01-05T12:00:00", "2026-01-10T12:00:00");

        Assert.Equal(108m, r.ExitPrice);
    }

    [Fact]
    public void Before_its_date_a_call_is_pending_with_a_provisional_mark_and_no_final_prices()
    {
        var r = Score(ClaimDirection.Bullish, "2026-01-06T12:00:00", "2026-03-01T12:00:00");

        Assert.Equal(ClaimStatus.Pending, r.Status);
        Assert.Equal(102m, r.EntryPrice);
        Assert.Null(r.ExitPrice);
        Assert.NotNull(r.ReturnPercent);                  // 102 to 120, as of the latest close
    }

    [Fact]
    public void A_bar_from_a_day_still_trading_is_not_used()
    {
        // 18:00 on the 13th is before that day's close, so the 12th is the latest finished day.
        var r = Score(ClaimDirection.Bullish, "2026-01-06T12:00:00", "2026-01-13T12:00:00", now: "2026-01-13T18:00:00");

        Assert.Equal(ClaimStatus.Pending, r.Status);
        Assert.Equal(5.8824m, r.ReturnPercent);           // 102 to 108
    }

    [Fact]
    public void No_prices_yet_stays_pending_then_becomes_unscorable()
    {
        Assert.Equal(ClaimStatus.Pending, ClaimScoring.Score(ClaimDirection.Bullish, null, Utc("2026-01-06T12:00:00"), Utc("2026-02-05T12:00:00"), [], Spy, Utc("2026-01-10T00:00:00")).Status);
        Assert.Equal(ClaimStatus.Unscorable, ClaimScoring.Score(ClaimDirection.Bullish, null, Utc("2026-01-06T12:00:00"), Utc("2026-02-05T12:00:00"), [], Spy, Utc("2026-02-20T00:00:00")).Status);
    }

    [Fact]
    public void A_stock_that_stops_trading_before_the_date_becomes_unscorable_after_a_grace_period()
    {
        var r = Score(ClaimDirection.Bullish, "2026-01-06T12:00:00", "2026-01-20T12:00:00", now: "2026-02-20T00:00:00");

        Assert.Equal(ClaimStatus.Unscorable, r.Status);
    }

    [Fact]
    public void A_target_already_passed_at_entry_is_ignored_rather_than_counted_as_hit()
    {
        // Entry is 102, so a bullish target of 50 and a bearish target of 500 say nothing.
        Assert.Null(Score(ClaimDirection.Bullish, "2026-01-06T12:00:00", "2026-01-13T12:00:00", target: 50).TargetHit);
        Assert.Null(Score(ClaimDirection.Bearish, "2026-01-06T12:00:00", "2026-01-13T12:00:00", target: 500).TargetHit);
    }

    [Fact]
    public void Target_counts_as_hit_on_intraday_highs_and_lows_inside_the_window_only()
    {
        Assert.True(Score(ClaimDirection.Bullish, "2026-01-06T12:00:00", "2026-01-13T12:00:00", target: 111).TargetHit);
        Assert.False(Score(ClaimDirection.Bullish, "2026-01-06T12:00:00", "2026-01-07T12:00:00", target: 111).TargetHit);
        Assert.True(Score(ClaimDirection.Bearish, "2026-01-06T12:00:00", "2026-01-15T12:00:00", target: 95).TargetHit);
        Assert.Null(Score(ClaimDirection.Bullish, "2026-01-06T12:00:00", "2026-01-13T12:00:00").TargetHit);
    }
}

public class CreatorRankingTests
{
    private static readonly Guid Alpha = Guid.NewGuid(), Beta = Guid.NewGuid();

    private static Claim Final(Guid channel, string ticker, string madeAt, decimal excess, decimal? ret = null, bool stated = true, int horizon = 30) => new()
    {
        ChannelId = channel,
        VideoId = Guid.NewGuid(),
        Ticker = ticker,
        Quote = "q",
        Direction = ClaimDirection.Bullish,
        QuoteVerified = true,
        Status = ClaimStatus.Final,
        MadeAt = Utc(madeAt),
        ResolvesAt = Utc(madeAt).AddDays(horizon),
        HorizonDays = horizon,
        HorizonStated = stated,
        ReturnPercent = ret ?? excess,
        ExcessReturnPercent = excess,
    };

    private static IReadOnlyList<CreatorStats> Rank(int min, params Claim[] claims) =>
        CreatorRanking.Build([new StatsChannel(Alpha, "Alpha", 1, 1), new StatsChannel(Beta, "Beta", 1, 1)], claims, min);

    [Fact]
    public void Creators_rank_by_return_over_the_benchmark_not_by_hit_rate()
    {
        // Alpha is right every time by riding the market. Beta is right half the time and beats it heavily.
        var claims = new[]
        {
            Final(Alpha, "SPY", "2026-01-01", excess: -1, ret: 2),
            Final(Alpha, "QQQ", "2026-01-01", excess: -1, ret: 2),
            Final(Beta, "AAA", "2026-01-01", excess: 20, ret: 25),
            Final(Beta, "BBB", "2026-01-01", excess: -5, ret: -3),
        };

        var rows = Rank(2, claims);

        Assert.Equal("Beta", rows[0].Title);
        Assert.Equal(1, rows[0].Rank);
        Assert.Equal(50m, rows[0].HitRate);
        Assert.Equal(100m, rows[1].HitRate);
        Assert.Equal(2, rows[1].Rank);
    }

    [Fact]
    public void Too_few_finished_calls_means_no_rank_however_good_they_are()
    {
        var rows = Rank(3, Final(Alpha, "AAA", "2026-01-01", excess: 90), Final(Beta, "B1", "2026-01-01", 1), Final(Beta, "B2", "2026-01-01", 1), Final(Beta, "B3", "2026-01-01", 1));

        Assert.Equal("Beta", rows[0].Title);
        Assert.Equal(1, rows[0].Rank);
        Assert.Null(rows[1].Rank);
        Assert.Equal(1, rows[1].FinalCalls);
    }

    [Fact]
    public void Repeating_an_open_call_counts_once_and_a_new_call_after_it_closes_counts_again()
    {
        var rows = Rank(1,
            Final(Alpha, "SPY", "2026-01-01", 1),
            Final(Alpha, "SPY", "2026-01-10", 1),
            Final(Alpha, "SPY", "2026-01-20", 1),
            Final(Alpha, "SPY", "2026-03-01", 1));

        Assert.Equal(2, rows.Single(r => r.Title == "Alpha").FinalCalls);
        Assert.Equal(4, rows.Single(r => r.Title == "Alpha").TotalCalls);
    }

    [Fact]
    public void Unverified_rejected_and_unscorable_calls_never_count()
    {
        var unverified = Final(Alpha, "A", "2026-01-01", 50);
        unverified.QuoteVerified = false;
        var rejected = Final(Alpha, "B", "2026-01-01", 50);
        rejected.Review = ClaimReview.Incorrect;
        var unscorable = Final(Alpha, "C", "2026-01-01", 50);
        unscorable.Status = ClaimStatus.Unscorable;

        Assert.Equal(0, Rank(1, unverified, rejected, unscorable).Single(r => r.Title == "Alpha").FinalCalls);
    }

    [Fact]
    public void A_vague_call_counts_half_as_much_as_a_specific_one()
    {
        var rows = Rank(1, Final(Alpha, "A", "2026-01-01", 10, stated: true), Final(Alpha, "B", "2026-01-01", 40, stated: false));

        // (10 x 1 + 40 x 0.5) / 1.5
        Assert.Equal(20m, rows.Single(r => r.Title == "Alpha").AverageExcessReturn);
    }

    [Fact]
    public void A_few_lucky_calls_earn_much_less_trust_than_the_same_record_over_many()
    {
        var few = Rank(1, Final(Alpha, "A", "2026-01-01", 20)).Single(r => r.Title == "Alpha");
        var many = Rank(1, Enumerable.Range(0, 30).Select(i => Final(Alpha, $"T{i}", "2026-01-01", 20)).ToArray()).Single(r => r.Title == "Alpha");

        Assert.True(many.TrustWeight > few.TrustWeight);
        Assert.Equal(1m, Rank(1).Single(r => r.Title == "Alpha").TrustWeight);
    }

    [Fact]
    public void Heads_up_counts_each_creator_once_on_their_latest_view_and_weights_by_trust()
    {
        var stats = new Dictionary<Guid, CreatorStats>
        {
            [Alpha] = new(Alpha, "Alpha", 0, 0, 0, 0, 0, null, null, null, null, null, 1, 2m),
            [Beta] = new(Beta, "Beta", 0, 0, 0, 0, 0, null, null, null, null, null, null, 0.5m),
        };
        var flipped = Final(Beta, "NVDA", "2026-01-02", 0);
        flipped.Direction = ClaimDirection.Bearish;

        var row = Assert.Single(DigestService.BuildSentiment(
            [Final(Alpha, "NVDA", "2026-01-01", 0), Final(Beta, "NVDA", "2026-01-01", 0), flipped], stats));

        Assert.Equal(1, row.BullishCalls);
        Assert.Equal(1, row.BearishCalls);
        Assert.Equal(0.6m, row.NetSentiment);             // (2 - 0.5) / 2.5
    }

    [Fact]
    public void Reversals_are_found_only_when_a_creator_flips_within_the_window()
    {
        var bear = Final(Alpha, "TSLA", "2026-01-10", 0);
        bear.Direction = ClaimDirection.Bearish;
        var laterBull = Final(Alpha, "TSLA", "2026-06-01", 0);

        var reversal = Assert.Single(ClaimQueries.FindReversals([Final(Alpha, "TSLA", "2026-01-01", 0), bear, laterBull], withinDays: 30));

        Assert.Equal("TSLA", reversal.Ticker);
        Assert.Equal(9, reversal.DaysApart);
    }
}
