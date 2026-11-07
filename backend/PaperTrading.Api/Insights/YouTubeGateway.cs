using System.Net;
using System.Text;
using YoutubeExplode;
using YoutubeExplode.Channels;
using YoutubeExplode.Common;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.ClosedCaptions;
using YoutubeExplode.Videos.Streams;
using PaperTrading.Api.Domain;

namespace PaperTrading.Api.Insights;

public record ChannelInfo(string Id, string Title);

public record UploadInfo(string VideoId, string Title, int? DurationSeconds);

public record TranscriptData(string Content, string Language, string Source);

/// <summary>YouTube can't be used right now, so fetching should pause rather than make things worse.</summary>
/// <param name="refused">True when YouTube said no, which needs a long pause. False for a network problem, which clears quickly.</param>
public class YouTubeBlockedException(string message, Exception? inner = null, bool refused = true) : Exception(message, inner)
{
    public bool WasRefused { get; } = refused;
}

public interface IYouTubeGateway
{
    /// <exception cref="TradingException">The link isn't a channel that can be found.</exception>
    Task<ChannelInfo> ResolveChannelAsync(string link, CancellationToken ct = default);

    /// <returns>Finished uploads, newest first. Live and upcoming streams are left out.</returns>
    Task<IReadOnlyList<UploadInfo>> ListUploadsAsync(string channelId, int count, CancellationToken ct = default);

    /// <returns>The publish time, or null when the video is unavailable.</returns>
    Task<DateTime?> GetPublishedAtAsync(string videoId, CancellationToken ct = default);

    /// <returns>The captions, or null when the video has none yet.</returns>
    Task<TranscriptData?> GetCaptionsAsync(string videoId, CancellationToken ct = default);

    /// <summary>Saves the audio track to a temporary file for speech-to-text.</summary>
    Task<string> DownloadAudioAsync(string videoId, CancellationToken ct = default);
}

public enum YouTubeLinkKind { ChannelId, Handle, Slug, User, Video }

public record YouTubeLink(YouTubeLinkKind Kind, string Value);

public static class YouTubeLinks
{
    private static readonly string[] Hosts = ["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be"];

    /// <summary>Works out what a pasted link refers to. Returns null when it isn't a YouTube channel or video.</summary>
    public static YouTubeLink? Parse(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length is 0 or > 300) return null;

        if (text.StartsWith('@')) return ChannelHandle.TryParse(text[1..]) is { } h ? new(YouTubeLinkKind.Handle, h.Value) : null;

        // A bare ID or name. Anything with a host must pass the host check below, as YoutubeExplode's parsers don't check it.
        if (!text.Contains('/') && !text.Contains('.'))
        {
            if (ChannelId.TryParse(text) is { } bareId) return new(YouTubeLinkKind.ChannelId, bareId.Value);
            return ChannelHandle.TryParse(text) is { } bare ? new(YouTubeLinkKind.Handle, bare.Value) : null;
        }

        var withScheme = text.Contains("://") ? text : "https://" + text;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) ||
            !Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return null;

        var absolute = uri.GetLeftPart(UriPartial.Path) + uri.Query;
        var path = uri.AbsolutePath;

        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/watch", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/live/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase))
            return VideoId.TryParse(absolute) is { } v ? new(YouTubeLinkKind.Video, v.Value) : null;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        var first = segments[0];

        if (first.StartsWith('@')) return ChannelHandle.TryParse(first[1..]) is { } handle ? new(YouTubeLinkKind.Handle, handle.Value) : null;
        if (segments.Length < 2) return null;

        var second = segments[1];
        return first.ToLowerInvariant() switch
        {
            "channel" => ChannelId.TryParse(second) is { } id ? new(YouTubeLinkKind.ChannelId, id.Value) : null,
            "c" => ChannelSlug.TryParse(second) is { } slug ? new(YouTubeLinkKind.Slug, slug.Value) : null,
            "user" => UserName.TryParse(second) is { } user ? new(YouTubeLinkKind.User, user.Value) : null,
            _ => null,
        };
    }
}

/// <summary>Talks to YouTube through YoutubeExplode, which reads the site's own unofficial endpoints.</summary>
public sealed class YouTubeGateway : IYouTubeGateway
{
    private const int SecondsPerTranscriptLine = 20;
    private readonly YoutubeClient _youtube = new(new HttpClient { Timeout = TimeSpan.FromSeconds(45) });

    public async Task<ChannelInfo> ResolveChannelAsync(string link, CancellationToken ct = default)
    {
        var parsed = YouTubeLinks.Parse(link)
            ?? throw new TradingException("That doesn't look like a YouTube channel link. Try something like youtube.com/@channelname.");

        return await Guarded(async () =>
        {
            var channel = parsed.Kind switch
            {
                YouTubeLinkKind.ChannelId => await _youtube.Channels.GetAsync(parsed.Value, ct),
                YouTubeLinkKind.Handle => await _youtube.Channels.GetByHandleAsync(parsed.Value, ct),
                YouTubeLinkKind.Slug => await _youtube.Channels.GetBySlugAsync(parsed.Value, ct),
                YouTubeLinkKind.User => await _youtube.Channels.GetByUserAsync(parsed.Value, ct),
                _ => await _youtube.Channels.GetAsync((await _youtube.Videos.GetAsync(parsed.Value, ct)).Author.ChannelId, ct),
            };
            return new ChannelInfo(channel.Id.Value, channel.Title);
        }, unavailable: () => new TradingException("We couldn't find that channel on YouTube. Check the link and try again."));
    }

    public Task<IReadOnlyList<UploadInfo>> ListUploadsAsync(string channelId, int count, CancellationToken ct = default) =>
        Guarded<IReadOnlyList<UploadInfo>>(async () =>
        {
            var uploads = new List<UploadInfo>(count);
            await foreach (var video in _youtube.Channels.GetUploadsAsync(channelId, ct))
            {
                if (video.Duration is not { } duration) continue;
                uploads.Add(new UploadInfo(video.Id.Value, video.Title, (int)duration.TotalSeconds));
                if (uploads.Count >= count) break;
            }
            return uploads;
        }, unavailable: () => new TradingException("That channel's uploads aren't available."));

    public async Task<DateTime?> GetPublishedAtAsync(string videoId, CancellationToken ct = default)
    {
        try
        {
            return await Guarded<DateTime?>(async () =>
            {
                var video = await _youtube.Videos.GetAsync(videoId, ct);
                return video.UploadDate.UtcDateTime;
            }, unavailable: () => new VideoUnavailableMarker());
        }
        catch (VideoUnavailableMarker)
        {
            return null;
        }
    }

    public async Task<TranscriptData?> GetCaptionsAsync(string videoId, CancellationToken ct = default)
    {
        try
        {
            return await Guarded<TranscriptData?>(async () =>
            {
                var manifest = await _youtube.Videos.ClosedCaptions.GetManifestAsync(videoId, ct);
                var info = PickTrack(manifest.Tracks);
                if (info is null) return null;

                var track = await _youtube.Videos.ClosedCaptions.GetAsync(info, ct);
                if (track.Captions.Count == 0) return null;

                return new TranscriptData(
                    Format(track.Captions.Select(c => (c.Offset, c.Text))),
                    info.Language.Code,
                    info.IsAutoGenerated ? "auto-captions" : "captions");
            }, unavailable: () => new VideoUnavailableMarker());
        }
        catch (VideoUnavailableMarker)
        {
            return null;
        }
    }

    public Task<string> DownloadAudioAsync(string videoId, CancellationToken ct = default) =>
        Guarded(async () =>
        {
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);
            // YouTube also serves auto-dubbed tracks, so this must be the original language, not just the smallest file.
            // Speech doesn't need quality, so among those the smallest downloads fastest.
            var stream = manifest.GetAudioOnlyStreams()
                .Where(s => s.IsAudioLanguageDefault != false)
                .OrderBy(s => s.Bitrate.BitsPerSecond)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("This video has no audio stream.");
            var path = Path.Combine(Path.GetTempPath(), $"papertrading-{videoId}-{Guid.NewGuid():N}.{stream.Container.Name}");
            await _youtube.Videos.Streams.DownloadAsync(stream, path, cancellationToken: ct);
            return path;
        }, unavailable: () => new InvalidOperationException("This video's audio isn't available."));

    /// <summary>Puts speech in [mm:ss] blocks, which keeps timestamps useful while using far fewer tokens than one per caption.</summary>
    public static string Format(IEnumerable<(TimeSpan Offset, string Text)> captions)
    {
        var output = new StringBuilder();
        var line = new StringBuilder();
        TimeSpan? lineStart = null;

        void Flush()
        {
            if (lineStart is { } start && line.Length > 0)
                output.Append('[').Append(Stamp(start)).Append("] ").AppendLine(line.ToString().Trim());
            line.Clear();
            lineStart = null;
        }

        foreach (var (offset, raw) in captions)
        {
            var text = raw.Replace('\n', ' ').Trim();
            if (text.Length == 0) continue;
            if (lineStart is { } start && (offset - start).TotalSeconds >= SecondsPerTranscriptLine) Flush();
            lineStart ??= offset;
            line.Append(text).Append(' ');
        }
        Flush();
        return output.ToString();
    }

    public static string Stamp(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    private static ClosedCaptionTrackInfo? PickTrack(IReadOnlyList<ClosedCaptionTrackInfo> tracks) =>
        tracks.Where(t => !t.IsAutoGenerated && IsEnglish(t)).FirstOrDefault()
        ?? tracks.Where(t => t.IsAutoGenerated && IsEnglish(t)).FirstOrDefault()
        ?? tracks.FirstOrDefault(t => !t.IsAutoGenerated)
        ?? tracks.FirstOrDefault();

    private static bool IsEnglish(ClosedCaptionTrackInfo track) =>
        track.Language.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sorts YouTube's failures into "this item is unavailable" and "we're being refused".</summary>
    public static async Task<T> Guarded<T>(Func<Task<T>> call, Func<Exception> unavailable)
    {
        try
        {
            return await call();
        }
        catch (RequestLimitExceededException ex)
        {
            throw new YouTubeBlockedException("YouTube is rate-limiting this machine.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw unavailable();
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode is 403 or 429)
        {
            throw new YouTubeBlockedException($"YouTube refused the request (HTTP {(int)ex.StatusCode!}).", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new YouTubeBlockedException("YouTube could not be reached.", ex, refused: false);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            // HttpClient reports its own timeout this way. Anything else is a real cancellation and passes through.
            throw new YouTubeBlockedException("YouTube timed out.", ex, refused: false);
        }
        catch (YoutubeExplodeException)
        {
            throw unavailable();
        }
    }

    private sealed class VideoUnavailableMarker : Exception;
}
