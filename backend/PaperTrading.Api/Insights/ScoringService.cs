using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;
using PaperTrading.Api.MarketData;

namespace PaperTrading.Api.Insights;

/// <summary>Keeps daily prices stored and scores claims against them.</summary>
public sealed class ScoringService(AppDbContext db, IPriceHistory history, IOptions<InsightsOptions> options, ILogger<ScoringService> log)
{
    /// <summary>Scores every claim that isn't final yet, and moves finished videos to Scored.</summary>
    /// <returns>The channels whose results changed.</returns>
    public async Task<IReadOnlyCollection<Guid>> RunAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var claims = await db.Claims.Where(c => c.Status == ClaimStatus.Pending).ToListAsync(ct);
        var changedChannels = new HashSet<Guid>();

        if (claims.Count > 0)
        {
            var benchmarkSymbol = options.Value.Benchmark;
            var earliest = DateOnly.FromDateTime(claims.Min(c => c.MadeAt)).AddDays(-5);
            var benchmark = await LoadBarsAsync(benchmarkSymbol, earliest, ct);

            foreach (var group in claims.GroupBy(c => c.Ticker))
            {
                IReadOnlyList<DailyBar> bars;
                try
                {
                    var from = DateOnly.FromDateTime(group.Min(c => c.MadeAt)).AddDays(-5);
                    bars = group.Key == benchmarkSymbol ? benchmark : await LoadBarsAsync(group.Key, from, ct);
                }
                catch (MarketDataUnavailableException ex)
                {
                    log.LogWarning("No prices for {Ticker}: {Message}", group.Key, ex.Message);
                    continue;
                }

                foreach (var claim in group)
                {
                    var score = ClaimScoring.Score(claim.Direction, claim.TargetPrice, claim.MadeAt, claim.ResolvesAt, bars, benchmark, now);
                    claim.Status = score.Status;
                    claim.EntryPrice = score.EntryPrice;
                    claim.ExitPrice = score.ExitPrice;
                    claim.BenchmarkEntryPrice = score.BenchmarkEntryPrice;
                    claim.BenchmarkExitPrice = score.BenchmarkExitPrice;
                    claim.ReturnPercent = score.ReturnPercent;
                    claim.ExcessReturnPercent = score.ExcessReturnPercent;
                    claim.TargetHit = score.TargetHit;
                    claim.ScoredAt = now;
                    changedChannels.Add(claim.ChannelId);
                }
            }
            await db.SaveChangesAsync(ct);
        }

        // A video is Scored once each of its claims has an entry price or has been given up on. Long calls can stay Pending for months.
        var extracted = await db.Videos.Where(v => v.Status == VideoStatus.Extracted).ToListAsync(ct);
        foreach (var video in extracted)
        {
            var videoClaims = await db.Claims.AsNoTracking().Where(c => c.VideoId == video.Id).ToListAsync(ct);
            if (videoClaims.All(c => c.EntryPrice is not null || c.Status == ClaimStatus.Unscorable))
            {
                video.Status = VideoStatus.Scored;
                changedChannels.Add(video.ChannelId);
            }
        }
        await db.SaveChangesAsync(ct);
        return changedChannels;
    }

    /// <summary>Refreshes stored prices for a symbol from Yahoo, then returns them. Stored prices serve when Yahoo is down.</summary>
    public async Task<IReadOnlyList<DailyBar>> LoadBarsAsync(string symbol, DateOnly from, CancellationToken ct)
    {
        var stored = await ReadBarsAsync(symbol, from, ct);
        try
        {
            var fetchFrom = stored.Count > 0 && stored[0].Date <= from ? stored[^1].Date.AddDays(-3) : from;
            var fresh = await history.GetDailyAsync(symbol, fetchFrom, ct);
            if (fresh.Count == 0) return stored;

            // A feed can repeat a date (e.g. the live session alongside the daily bar), so keep the last one per day.
            fresh = fresh.GroupBy(b => b.Date).Select(g => g.Last()).ToList();
            var dates = fresh.Select(b => b.Date).ToList();
            await db.PriceBars.Where(b => b.Symbol == symbol && dates.Contains(b.Date)).ExecuteDeleteAsync(ct);
            // Bars left tracked by an earlier failed save would clash with the new ones.
            foreach (var entry in db.ChangeTracker.Entries<PriceBar>().ToList()) entry.State = EntityState.Detached;
            db.PriceBars.AddRange(fresh.Select(b => new PriceBar
            {
                Symbol = symbol, Date = b.Date, High = b.High, Low = b.Low, Close = b.Close, ClosesAt = b.ClosesAt,
            }));
            await db.SaveChangesAsync(ct);
            foreach (var entry in db.ChangeTracker.Entries<PriceBar>().ToList()) entry.State = EntityState.Detached;
            return await ReadBarsAsync(symbol, from, ct);
        }
        catch (MarketDataUnavailableException) when (stored.Count > 0)
        {
            return stored;
        }
    }

    private async Task<List<DailyBar>> ReadBarsAsync(string symbol, DateOnly from, CancellationToken ct)
    {
        var rows = await db.PriceBars.AsNoTracking()
            .Where(b => b.Symbol == symbol && b.Date >= from)
            .OrderBy(b => b.Date)
            .ToListAsync(ct);
        return rows.Select(b => new DailyBar(b.Date, b.High, b.Low, b.Close, b.ClosesAt)).ToList();
    }
}
