using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;

namespace PaperTrading.Api.Insights;

/// <summary>Reads claims and track records for the API.</summary>
public sealed class ClaimQueries(AppDbContext db, IOptions<InsightsOptions> options)
{
    private static readonly VideoStatus[] Processed = [VideoStatus.Extracted, VideoStatus.Scored];

    public async Task<IReadOnlyList<CreatorStats>> LeaderboardAsync(CancellationToken ct = default)
    {
        var channels = await db.Channels.AsNoTracking().ToListAsync(ct);
        var videos = await db.Videos.AsNoTracking().Select(v => new { v.ChannelId, v.Status }).ToListAsync(ct);
        var claims = await db.Claims.AsNoTracking().ToListAsync(ct);

        var rows = channels.Select(c => new StatsChannel(
            c.Id, c.Title,
            videos.Count(v => v.ChannelId == c.Id),
            videos.Count(v => v.ChannelId == c.Id && Processed.Contains(v.Status))));
        return CreatorRanking.Build(rows, claims, options.Value.MinCallsToRank);
    }

    public async Task<List<ClaimDto>> ClaimsAsync(IQueryable<Claim> query, CancellationToken ct = default)
    {
        var rows = await query.AsNoTracking()
            .Join(db.Videos.AsNoTracking(), c => c.VideoId, v => v.Id, (c, v) => new { c, v })
            .Join(db.Channels.AsNoTracking(), x => x.c.ChannelId, ch => ch.Id, (x, ch) => new { x.c, x.v, ch })
            .OrderByDescending(x => x.c.MadeAt)
            .ToListAsync(ct);

        return rows.Select(x => ToDto(x.c, x.v, x.ch)).ToList();
    }

    public async Task<ChannelDto> ToChannelDtoAsync(Channel channel, Guid userId, CreatorStats? stats, CancellationToken ct = default)
    {
        var statuses = await db.Videos.AsNoTracking().Where(v => v.ChannelId == channel.Id).Select(v => v.Status).ToListAsync(ct);
        var following = await db.ChannelFollows.AnyAsync(f => f.UserId == userId && f.ChannelId == channel.Id, ct);
        var counts = new VideoCounts(
            statuses.Count,
            statuses.Count(s => s == VideoStatus.Discovered),
            statuses.Count(s => s == VideoStatus.TranscriptFetched),
            statuses.Count(s => s == VideoStatus.Extracted),
            statuses.Count(s => s == VideoStatus.Scored),
            statuses.Count(s => s == VideoStatus.NoCaptions),
            statuses.Count(s => s == VideoStatus.Failed));

        stats ??= new CreatorStats(channel.Id, channel.Title, 0, 0, 0, 0, 0, null, null, null, null, null, null, 1);
        return new ChannelDto(channel.Id, channel.YouTubeChannelId, channel.Title, channel.AddedAt, channel.LastPolledAt, channel.LastPollError, following, counts, stats);
    }

    /// <summary>Finds where a creator changed their mind on a ticker soon after calling it. Comes straight from stored dates, with no model.</summary>
    public static IReadOnlyList<ReversalDto> FindReversals(IEnumerable<Claim> claims, int withinDays = 30)
    {
        var reversals = new List<ReversalDto>();
        foreach (var group in claims.Where(c => c.QuoteVerified && c.Review != ClaimReview.Incorrect).GroupBy(c => c.Ticker))
        {
            Claim? previous = null;
            foreach (var claim in group.OrderBy(c => c.MadeAt))
            {
                if (previous is not null && previous.Direction != claim.Direction)
                {
                    var days = (int)(claim.MadeAt - previous.MadeAt).TotalDays;
                    if (days <= withinDays)
                        reversals.Add(new ReversalDto(group.Key, previous.MadeAt, previous.Direction, claim.MadeAt, claim.Direction, days));
                }
                previous = claim;
            }
        }
        return reversals.OrderByDescending(r => r.SecondAt).ToList();
    }

    public static ClaimDto ToDto(Claim c, Video v, Channel ch) => new(
        c.Id, c.VideoId, c.ChannelId, ch.Title, v.Title, v.YouTubeVideoId,
        c.Ticker, c.Direction, c.TargetPrice, c.HorizonDays, c.HorizonStated, c.Confidence,
        c.Quote, c.TimestampSeconds, c.Reasoning, c.Speaker, c.QuoteVerified,
        c.MadeAt, c.ResolvesAt, c.Status, c.EntryPrice, c.ExitPrice,
        c.ReturnPercent, c.ExcessReturnPercent, c.TargetHit, c.Review);
}
