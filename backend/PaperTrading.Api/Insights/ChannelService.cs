using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;

namespace PaperTrading.Api.Insights;

public sealed class ChannelService(AppDbContext db, IYouTubeGateway youtube, IOptions<InsightsOptions> options)
{
    private readonly InsightsOptions _options = options.Value;

    /// <summary>Follows a pasted channel and queues its backfill. A channel someone already added is reused, not reprocessed.</summary>
    public async Task<Channel> AddAsync(Guid userId, string link, int? backfill, CancellationToken ct = default)
    {
        var count = Math.Clamp(backfill ?? _options.DefaultBackfill, _options.MinBackfill, _options.MaxBackfill);
        var info = await youtube.ResolveChannelAsync(link, ct);

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.YouTubeChannelId == info.Id, ct);
        if (channel is null)
        {
            channel = new Channel { YouTubeChannelId = info.Id, Title = info.Title, BackfillCount = count };
            db.Channels.Add(channel);
        }
        else if (count > channel.BackfillCount)
        {
            // Asking for more history than before re-runs discovery. Videos already known are skipped.
            channel.BackfillCount = count;
            channel.LastPolledAt = null;
        }

        if (!await db.ChannelFollows.AnyAsync(f => f.UserId == userId && f.ChannelId == channel.Id, ct))
            db.ChannelFollows.Add(new ChannelFollow { UserId = userId, ChannelId = channel.Id });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with someone adding the same channel.
            throw new TradingException("That channel was just added. Refresh to see it.", StatusCodes.Status409Conflict);
        }
        return channel;
    }

    /// <summary>Puts a channel's failed videos back in the queue, for example once captions have appeared.</summary>
    public async Task<int> RetryAsync(Guid channelId, CancellationToken ct = default)
    {
        var videos = await db.Videos
            .Where(v => v.ChannelId == channelId && (v.Status == VideoStatus.NoCaptions || v.Status == VideoStatus.Failed))
            .ToListAsync(ct);
        var hasTranscript = await db.Transcripts.Where(t => videos.Select(v => v.Id).Contains(t.VideoId)).Select(t => t.VideoId).ToListAsync(ct);

        foreach (var video in videos)
        {
            video.Status = hasTranscript.Contains(video.Id) ? VideoStatus.TranscriptFetched : VideoStatus.Discovered;
            video.Attempts = 0;
            video.LastError = null;
            video.NextAttemptAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return videos.Count;
    }
}
