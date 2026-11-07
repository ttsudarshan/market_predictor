using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;
using PaperTrading.Api.MarketData;

namespace PaperTrading.Api.Insights;

/// <summary>The individual pipeline steps. No lock or transaction is held during a network or model call.</summary>
public sealed class PipelineSteps(
    AppDbContext db, IYouTubeGateway youtube, IClaimExtractor extractor, ILlmClient llm, IPriceHistory prices,
    IOptions<InsightsOptions> options, ILogger<PipelineSteps> log)
{
    private readonly InsightsOptions _options = options.Value;

    /// <summary>Adds any uploads that aren't known yet. Safe to repeat.</summary>
    public async Task<int> DiscoverAsync(Channel channel, int count, CancellationToken ct)
    {
        var uploads = await youtube.ListUploadsAsync(channel.YouTubeChannelId, count, ct);
        var ids = uploads.Select(u => u.VideoId).ToList();
        var known = await db.Videos.Where(v => ids.Contains(v.YouTubeVideoId)).Select(v => v.YouTubeVideoId).ToListAsync(ct);

        var fresh = uploads.Where(u => !known.Contains(u.VideoId)).Select(u => new Video
        {
            ChannelId = channel.Id,
            YouTubeVideoId = u.VideoId,
            Title = u.Title,
            DurationSeconds = u.DurationSeconds,
        }).ToList();

        db.Videos.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }

    /// <summary>Checks one channel for uploads. It keeps asking for the full backfill until a channel has any videos.</summary>
    /// <returns>How many new videos were found.</returns>
    public async Task<int> PollChannelAsync(Channel channel, CancellationToken ct)
    {
        var needsBackfill = channel.LastPolledAt is null || !await db.Videos.AnyAsync(v => v.ChannelId == channel.Id, ct);
        var count = needsBackfill ? channel.BackfillCount : _options.NewVideosPerPoll;
        try
        {
            var added = await DiscoverAsync(channel, count, ct);
            channel.LastPollError = null;
            channel.LastPolledAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return added;
        }
        catch (TradingException ex)
        {
            channel.LastPollError = ex.Message;
            channel.LastPolledAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return 0;
        }
    }

    /// <summary>Fetches the transcript for the next waiting video.</summary>
    /// <returns>The channel affected, or null when nothing is waiting.</returns>
    public async Task<Guid?> FetchNextTranscriptAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var video = await db.Videos
            .Where(v => v.Status == VideoStatus.Discovered && v.NextAttemptAt <= now)
            .OrderBy(v => v.Attempts).ThenBy(v => v.DiscoveredAt)
            .FirstOrDefaultAsync(ct);
        if (video is null) return null;

        TranscriptData? transcript;
        try
        {
            video.PublishedAt ??= await youtube.GetPublishedAtAsync(video.YouTubeVideoId, ct);
            if (video.PublishedAt is null)
            {
                await RetryOrGiveUpAsync(video, VideoStatus.Failed, "The video isn't available.", ct);
                return video.ChannelId;
            }

            transcript = await youtube.GetCaptionsAsync(video.YouTubeVideoId, ct);
            if (transcript is null && video.Attempts + 1 >= _options.MaxTranscriptAttempts && !string.IsNullOrWhiteSpace(_options.WhisperCommand))
                transcript = await TranscribeAudioAsync(video.YouTubeVideoId, ct);
        }
        catch (YouTubeBlockedException ex)
        {
            // Sends this video to the back of the queue, so one that keeps failing can't hold up the rest.
            // It doesn't use up an attempt, because this says nothing about whether the video has captions.
            video.LastError = ex.Message;
            video.NextAttemptAt = DateTime.UtcNow.AddMinutes(10);
            await db.SaveChangesAsync(ct);
            throw;
        }

        if (transcript is null)
        {
            await RetryOrGiveUpAsync(video, VideoStatus.NoCaptions, "No captions are available yet.", ct);
            return video.ChannelId;
        }

        db.Transcripts.Add(new Transcript
        {
            VideoId = video.Id,
            Content = transcript.Content,
            Language = transcript.Language,
            Source = transcript.Source,
        });
        video.Status = VideoStatus.TranscriptFetched;
        video.Attempts = 0;
        video.LastError = null;
        video.NextAttemptAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return video.ChannelId;
    }

    /// <summary>Sends the next waiting transcript to the model and saves its calls.</summary>
    /// <returns>The channel affected, or null when nothing is waiting.</returns>
    public async Task<Guid?> ExtractNextAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var candidate = await db.Videos.AsNoTracking()
            .Where(v => v.Status == VideoStatus.TranscriptFetched && v.NextAttemptAt <= now)
            .OrderBy(v => v.Attempts).ThenByDescending(v => v.PublishedAt)
            .Select(v => (Guid?)v.Id)
            .FirstOrDefaultAsync(ct);
        if (candidate is not { } id) return null;

        // Takes the video in one atomic update, so two workers can't both pay to process it.
        var taken = await db.Videos
            .Where(v => v.Id == id && v.Status == VideoStatus.TranscriptFetched && v.NextAttemptAt <= now)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.NextAttemptAt, now.AddMinutes(20)), ct);
        if (taken == 0) return null;

        var video = await db.Videos.FirstAsync(v => v.Id == id, ct);

        var transcript = await db.Transcripts.AsNoTracking().FirstOrDefaultAsync(t => t.VideoId == video.Id, ct);
        if (transcript is null)
        {
            // Retention removed it before extraction ran. Fetch it again.
            video.Status = VideoStatus.Discovered;
            video.NextAttemptAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return video.ChannelId;
        }

        var channel = await db.Channels.AsNoTracking().FirstAsync(c => c.Id == video.ChannelId, ct);

        ExtractionResult result;
        try
        {
            result = await extractor.ExtractAsync(channel.Title, video.Title, video.PublishedAt!.Value, transcript.Content, ct);
        }
        catch (LlmUnavailableException ex)
        {
            video.LastError = ex.Message;
            video.NextAttemptAt = DateTime.UtcNow.AddMinutes(5);
            await db.SaveChangesAsync(ct);
            throw;
        }
        catch (LlmRejectedException ex)
        {
            log.LogWarning("Extraction refused for video {Video}: {Message}", video.YouTubeVideoId, ex.Message);
            await RetryOrGiveUpAsync(video, VideoStatus.Failed, ex.Message, ct, ex.IsRetryable ? _options.MaxExtractionAttempts : 1);
            return video.ChannelId;
        }

        var unknown = await FindUnknownTickersAsync(result.Claims, video.PublishedAt!.Value, ct);

        // Replaces any earlier claims in one write, so a retry can never double-count.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Claims.Where(c => c.VideoId == video.Id).ExecuteDeleteAsync(ct);
        db.Claims.AddRange(result.Claims.Where(c => !unknown.Contains(c.Ticker)).Select(c => ToClaim(video, c)));
        video.RejectedClaims = result.Claims.Count(c => unknown.Contains(c.Ticker));
        video.Status = VideoStatus.Extracted;
        video.Attempts = 0;
        video.LastError = null;
        video.ExtractionModel = llm.Model;
        video.PromptVersion = ClaimExtractor.PromptVersion;
        video.InputTokens = result.InputTokens;
        video.OutputTokens = result.OutputTokens;
        video.NextAttemptAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return video.ChannelId;
    }

    /// <summary>Deletes transcripts past the retention period. Claims, quotes and scores stay.</summary>
    public Task<int> PruneTranscriptsAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
        var finished = db.Videos.Where(v => v.Status == VideoStatus.Extracted || v.Status == VideoStatus.Scored).Select(v => v.Id);
        return db.Transcripts.Where(t => t.FetchedAt < cutoff && finished.Contains(t.VideoId)).ExecuteDeleteAsync(ct);
    }

    /// <summary>Finds tickers the model made up. If Yahoo can't answer, they're kept and left for scoring to judge.</summary>
    private async Task<HashSet<string>> FindUnknownTickersAsync(IReadOnlyList<ExtractedClaim> claims, DateTime publishedAt, CancellationToken ct)
    {
        var unknown = new HashSet<string>();
        foreach (var ticker in claims.Select(c => c.Ticker).Distinct())
        {
            try
            {
                var bars = await prices.GetDailyAsync(ticker, DateOnly.FromDateTime(publishedAt).AddDays(-7), ct);
                if (bars.Count == 0) unknown.Add(ticker);
            }
            catch (MarketDataUnavailableException ex)
            {
                log.LogInformation("Couldn't check ticker {Ticker}: {Message}", ticker, ex.Message);
            }
        }
        return unknown;
    }

    private Claim ToClaim(Video video, ExtractedClaim c) => new()
    {
        VideoId = video.Id,
        ChannelId = video.ChannelId,
        Ticker = c.Ticker,
        Direction = c.Direction,
        TargetPrice = c.TargetPrice,
        HorizonDays = c.HorizonDays ?? _options.DefaultHorizonDays,
        HorizonStated = c.HorizonDays is not null,
        // A call with no timeframe is low confidence however firmly it was said.
        Confidence = c.HorizonDays is null ? ClaimConfidence.Low : c.Confidence,
        Quote = c.Quote,
        TimestampSeconds = c.TimestampSeconds,
        Reasoning = c.Reasoning,
        Speaker = c.Speaker,
        QuoteVerified = c.QuoteVerified,
        MadeAt = video.PublishedAt!.Value,
        ResolvesAt = video.PublishedAt!.Value.AddDays(c.HorizonDays ?? _options.DefaultHorizonDays),
    };

    private async Task RetryOrGiveUpAsync(Video video, VideoStatus giveUpStatus, string reason, CancellationToken ct, int? maxAttempts = null)
    {
        video.Attempts++;
        video.LastError = reason;
        if (video.Attempts >= (maxAttempts ?? _options.MaxTranscriptAttempts))
        {
            video.Status = giveUpStatus;
        }
        else
        {
            // Captions usually show up within hours, so wait longer each time: 1, 4, 16, 64 hours.
            video.NextAttemptAt = DateTime.UtcNow.AddHours(Math.Pow(4, video.Attempts - 1));
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<TranscriptData?> TranscribeAudioAsync(string videoId, CancellationToken ct)
    {
        string? audio = null;
        try
        {
            audio = await youtube.DownloadAudioAsync(videoId, ct);
            return await RunWhisperAsync(_options.WhisperCommand!, audio, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or IOException)
        {
            log.LogWarning("Speech-to-text failed for {Video}: {Message}", videoId, ex.Message);
            return null;
        }
        finally
        {
            if (audio is not null) File.Delete(audio);
        }
    }

    /// <summary>Runs the configured speech-to-text command.</summary>
    public static async Task<TranscriptData> RunWhisperAsync(string commandTemplate, string audioPath, CancellationToken ct)
    {
        var parts = commandTemplate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = new ProcessStartInfo(parts[0]) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var part in parts.Skip(1)) start.ArgumentList.Add(part.Replace("{audio}", audioPath));

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start the speech-to-text command.");
        var errors = process.StandardError.ReadToEndAsync(ct);
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Speech-to-text exited with code {process.ExitCode}: {await errors}");

        var segments = JsonSerializer.Deserialize<WhisperSegment[]>(output, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var content = YouTubeGateway.Format(segments.Select(s => (TimeSpan.FromSeconds(s.Start), s.Text)));
        return new TranscriptData(content, "auto", "speech-to-text");
    }

    private sealed record WhisperSegment(double Start, string Text);
}
