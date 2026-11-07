using PaperTrading.Api.Domain;
using PaperTrading.Api.Insights;
using PaperTrading.Api.MarketData;

namespace PaperTrading.Tests;

public sealed class FakeYouTube : IYouTubeGateway
{
    public Dictionary<string, ChannelInfo> Channels { get; } = new();
    public List<UploadInfo> Uploads { get; } = [];
    public Dictionary<string, DateTime> Published { get; } = new();
    public Dictionary<string, TranscriptData> Captions { get; } = new();
    public bool Blocked { get; set; }
    public HashSet<string> BlockedVideos { get; } = [];
    public int CaptionCalls { get; private set; }
    public Exception? UploadsFailure { get; set; }
    public int LastRequestedCount { get; set; }

    public Task<ChannelInfo> ResolveChannelAsync(string link, CancellationToken ct = default) =>
        Channels.TryGetValue(link, out var c) ? Task.FromResult(c) : throw new TradingException("We couldn't find that channel on YouTube.");

    public Task<IReadOnlyList<UploadInfo>> ListUploadsAsync(string channelId, int count, CancellationToken ct = default)
    {
        LastRequestedCount = count;
        if (UploadsFailure is not null) throw UploadsFailure;
        return Task.FromResult<IReadOnlyList<UploadInfo>>(Uploads.Take(count).ToList());
    }

    public Task<DateTime?> GetPublishedAtAsync(string videoId, CancellationToken ct = default)
    {
        if (Blocked || BlockedVideos.Contains(videoId)) throw new YouTubeBlockedException("Blocked.");
        return Task.FromResult<DateTime?>(Published.TryGetValue(videoId, out var d) ? d : null);
    }

    public Task<TranscriptData?> GetCaptionsAsync(string videoId, CancellationToken ct = default)
    {
        CaptionCalls++;
        return Task.FromResult(Captions.GetValueOrDefault(videoId));
    }

    public Task<string> DownloadAudioAsync(string videoId, CancellationToken ct = default) =>
        throw new InvalidOperationException("No audio in tests.");
}

public sealed class FakeLlm : ILlmClient
{
    public Func<string, string, LlmResult>? Handler { get; set; }
    public bool IsConfigured { get; set; } = true;
    public string Model => "fake-model";
    public int Calls { get; private set; }
    public string? LastUserMessage { get; private set; }

    public Task<LlmResult> CompleteJsonAsync(string system, string user, object schema, CancellationToken ct = default)
    {
        Calls++;
        LastUserMessage = user;
        return Task.FromResult(Handler?.Invoke(system, user) ?? new LlmResult("""{"claims":[]}""", 100, 10));
    }
}

public sealed class FakePrices : IPriceHistory
{
    public Dictionary<string, List<DailyBar>> Bars { get; } = new();
    public bool Failing { get; set; }

    public Task<IReadOnlyList<DailyBar>> GetDailyAsync(string symbol, DateOnly from, CancellationToken ct = default)
    {
        if (Failing) throw new MarketDataUnavailableException("Prices down.");
        IReadOnlyList<DailyBar> bars = Bars.TryGetValue(symbol, out var list) ? list.Where(b => b.Date >= from).ToList() : [];
        return Task.FromResult(bars);
    }
}

internal static class Bars
{
    /// <summary>A bar closing at 21:00 UTC, the usual US close in winter.</summary>
    public static DailyBar On(string date, decimal close, decimal? high = null, decimal? low = null)
    {
        var d = DateOnly.Parse(date);
        return new DailyBar(d, high ?? close, low ?? close, close,
            DateTime.SpecifyKind(d.ToDateTime(new TimeOnly(21, 0)), DateTimeKind.Utc));
    }

    public static DateTime Utc(string value) => DateTime.SpecifyKind(DateTime.Parse(value), DateTimeKind.Utc);
}
