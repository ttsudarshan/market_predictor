using PaperTrading.Api.Domain;
using PaperTrading.Api.MarketData;

namespace PaperTrading.Api.Insights;

public record ScoreResult(
    ClaimStatus Status, decimal? EntryPrice, decimal? ExitPrice, decimal? BenchmarkEntryPrice, decimal? BenchmarkExitPrice,
    decimal? ReturnPercent, decimal? ExcessReturnPercent, bool? TargetHit);

/// <summary>
/// The scoring rules, as plain arithmetic with no model involved, so the same prices always give the same score.
/// </summary>
public static class ClaimScoring
{
    /// <summary>How long after its date a claim with no price is given up on, such as for a delisted stock.</summary>
    private const int GiveUpAfterDays = 14;

    /// <summary>
    /// Entry is the first daily close at or after publication, so a call never gets credit for a move that had
    /// already happened when viewers could act on it. Exit is the first close on or after the resolve date.
    /// Until then the claim is Pending, and its figures are a provisional mark to the latest close.
    /// </summary>
    /// <param name="bars">Daily prices for the claim's ticker, oldest first.</param>
    /// <param name="benchmark">Daily prices for the benchmark, oldest first.</param>
    public static ScoreResult Score(
        ClaimDirection direction, decimal? target, DateTime madeAt, DateTime resolvesAt,
        IReadOnlyList<DailyBar> bars, IReadOnlyList<DailyBar> benchmark, DateTime now)
    {
        var closed = bars.Where(b => b.ClosesAt <= now).ToList();
        var entry = closed.FirstOrDefault(b => b.ClosesAt >= madeAt);
        if (entry is null)
        {
            var hopeless = now - madeAt > TimeSpan.FromDays(GiveUpAfterDays);
            return new ScoreResult(hopeless ? ClaimStatus.Unscorable : ClaimStatus.Pending, null, null, null, null, null, null, null);
        }

        // Prices are dated by the New York trading day, so the resolve date has to be too.
        var resolveDate = NyseCalendar.DateInNewYork(resolvesAt);
        var exit = closed.FirstOrDefault(b => b.Date >= resolveDate);
        var isFinal = exit is not null;

        if (!isFinal && now - resolvesAt > TimeSpan.FromDays(GiveUpAfterDays))
            return new ScoreResult(ClaimStatus.Unscorable, entry.Close, null, null, null, null, null, null);

        var mark = exit ?? closed[^1];
        var sign = direction == ClaimDirection.Bullish ? 1m : -1m;
        var move = PercentChange(entry.Close, mark.Close);

        var closedBenchmark = benchmark.Where(b => b.ClosesAt <= now).ToList();
        var benchmarkEntry = closedBenchmark.FirstOrDefault(b => b.ClosesAt >= madeAt);
        var benchmarkMark = closedBenchmark.FirstOrDefault(b => b.Date >= mark.Date) ?? closedBenchmark.LastOrDefault();

        decimal? excess = benchmarkEntry is not null && benchmarkMark is not null
            ? sign * (move - PercentChange(benchmarkEntry.Close, benchmarkMark.Close))
            : null;

        // A target already passed when the call was made says nothing, so it can't count as hit.
        bool? targetHit = null;
        if (target is { } price && (direction == ClaimDirection.Bullish ? price > entry.Close : price < entry.Close))
        {
            var window = closed.Where(b => b.Date > entry.Date && b.Date <= mark.Date).ToList();
            targetHit = direction == ClaimDirection.Bullish
                ? window.Any(b => b.High >= price)
                : window.Any(b => b.Low <= price);
        }

        // A final score needs its benchmark too, or it would rank on incomplete figures.
        var status = isFinal && excess is not null ? ClaimStatus.Final : ClaimStatus.Pending;
        return new ScoreResult(
            status, entry.Close, status == ClaimStatus.Final ? mark.Close : null,
            benchmarkEntry?.Close, status == ClaimStatus.Final ? benchmarkMark?.Close : null,
            Math.Round(sign * move, 4), excess is { } e ? Math.Round(e, 4) : null, targetHit);
    }

    private static decimal PercentChange(decimal from, decimal to) => from == 0 ? 0 : (to - from) / from * 100;
}

public record CreatorStats(
    Guid ChannelId, string Title, int Videos, int VideosProcessed, int TotalCalls, int FinalCalls, int PendingCalls,
    decimal? HitRate, decimal? BeatBenchmarkRate, decimal? AverageReturn, decimal? AverageExcessReturn,
    decimal? TargetHitRate, int? Rank, decimal TrustWeight);

public record StatsChannel(Guid Id, string Title, int Videos, int VideosProcessed);

/// <summary>Works out track records. These are computed from claims each time, so they can't drift out of date.</summary>
public static class CreatorRanking
{
    private const decimal VagueCallWeight = 0.5m;
    private const int ShrinkageCalls = 10;
    private const decimal ExcessPerWeightUnit = 10m;

    /// <summary>Calls that count: confirmed by their quote, not rejected in review, and not a repeat.</summary>
    public static List<Claim> Countable(IEnumerable<Claim> claims)
    {
        var counted = new List<Claim>();
        foreach (var group in claims
            .Where(c => c.QuoteVerified && c.Review != ClaimReview.Incorrect && c.Status != ClaimStatus.Unscorable)
            .GroupBy(c => (c.ChannelId, c.Ticker, c.Direction)))
        {
            // Repeating a call while it's still open is one call. Otherwise fifty "SPY goes up" videos would count fifty times.
            DateTime? openUntil = null;
            foreach (var claim in group.OrderBy(c => c.MadeAt))
            {
                if (openUntil is { } until && claim.MadeAt < until) continue;
                counted.Add(claim);
                openUntil = claim.ResolvesAt;
            }
        }
        return counted;
    }

    public static IReadOnlyList<CreatorStats> Build(IEnumerable<StatsChannel> channels, IEnumerable<Claim> claims, int minCallsToRank)
    {
        var all = claims.ToList();
        var counted = Countable(all).ToLookup(c => c.ChannelId);
        var totals = all.ToLookup(c => c.ChannelId);

        var rows = channels.Select(ch =>
        {
            var mine = counted[ch.Id].ToList();
            var final = mine.Where(c => c.Status == ClaimStatus.Final && c.ExcessReturnPercent is not null).ToList();
            var withTarget = final.Where(c => c.TargetHit is not null).ToList();
            var totalWeight = final.Sum(Weight);

            decimal? averageExcess = totalWeight == 0 ? null : final.Sum(c => Weight(c) * c.ExcessReturnPercent!.Value) / totalWeight;

            // Small samples are pulled towards zero, so a lucky few calls can't earn much trust.
            var shrunk = (averageExcess ?? 0) * final.Count / (final.Count + ShrinkageCalls);
            var trust = Math.Clamp(1 + shrunk / ExcessPerWeightUnit, 0.25m, 2m);

            return new CreatorStats(
                ch.Id, ch.Title, ch.Videos, ch.VideosProcessed, totals[ch.Id].Count(), final.Count,
                mine.Count(c => c.Status == ClaimStatus.Pending),
                Rate(final, c => c.ReturnPercent > 0),
                Rate(final, c => c.ExcessReturnPercent > 0),
                final.Count == 0 ? null : Math.Round(final.Average(c => c.ReturnPercent ?? 0), 2),
                averageExcess is { } avg ? Math.Round(avg, 2) : null,
                Rate(withTarget, c => c.TargetHit == true),
                null,
                Math.Round(trust, 3));
        }).ToList();

        var rank = 1;
        var ranked = rows
            .Where(r => r.FinalCalls >= minCallsToRank && r.AverageExcessReturn is not null)
            .OrderByDescending(r => r.AverageExcessReturn)
            .ThenByDescending(r => r.FinalCalls)
            .Select(r => r with { Rank = rank++ })
            .ToList();

        var unranked = rows.Where(r => !ranked.Any(x => x.ChannelId == r.ChannelId)).OrderByDescending(r => r.FinalCalls).ThenBy(r => r.Title);
        return [.. ranked, .. unranked];
    }

    /// <summary>A vague call with no stated horizon counts half as much as a specific one.</summary>
    private static decimal Weight(Claim c) => c.HorizonStated ? 1m : VagueCallWeight;

    private static decimal? Rate(List<Claim> claims, Func<Claim, bool> test) =>
        claims.Count == 0 ? null : Math.Round(claims.Count(test) / (decimal)claims.Count * 100, 1);
}
