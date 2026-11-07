using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Data;
using PaperTrading.Api.Hubs;

namespace PaperTrading.Api.Insights;

/// <summary>Shared pipeline state, shown on the Creators page.</summary>
public sealed class PipelineState
{
    public DateTime? YouTubePausedUntil { get; set; }
    public string? YouTubePauseReason { get; set; }
    public DateTime? LlmPausedUntil { get; set; }
    public string? LlmPauseReason { get; set; }

    private readonly object _gate = new();
    private CancellationTokenSource _run = Stopped();

    /// <summary>Whether the pipeline is fetching and processing. Off at launch unless configured otherwise.</summary>
    public bool Running { get { lock (_gate) return !_run.IsCancellationRequested; } }

    /// <summary>Cancelled the moment the pipeline is stopped, which aborts any request in flight.</summary>
    public CancellationToken RunToken { get { lock (_gate) return _run.Token; } }

    public void Start()
    {
        lock (_gate) if (_run.IsCancellationRequested) _run = new CancellationTokenSource();
    }

    public void Stop()
    {
        lock (_gate) _run.Cancel();
    }

    private static CancellationTokenSource Stopped()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts;
    }
}

/// <summary>
/// Runs the pipeline in separate loops, so a slow model can't hold up transcripts and a YouTube block
/// can't hold up scoring. Each loop picks up from the database, so a restart loses nothing.
/// </summary>
public sealed class InsightsPipelineService(
    IServiceScopeFactory scopes, ILlmClient llm, PipelineState state, IHubContext<MarketHub> hub,
    IOptions<InsightsOptions> options, ILogger<InsightsPipelineService> log) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(10);
    private readonly InsightsOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.StartOnLaunch) state.Start();
        var extractors = Enumerable.Range(0, Math.Max(_options.ExtractionConcurrency, 1)).Select(_ => ExtractLoopAsync(stoppingToken));
        return Task.WhenAll([PollLoopAsync(stoppingToken), TranscriptLoopAsync(stoppingToken), ScoreLoopAsync(stoppingToken), MaintenanceLoopAsync(stoppingToken), .. extractors]);
    }

    private Task Announce(Guid channelId, CancellationToken ct) =>
        hub.Clients.All.SendAsync("pipelineUpdated", channelId, ct);

    /// <summary>Checks channels for new uploads. The RSS feeds don't respond, so this reads the uploads list.</summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var run = RunScope(ct);
            await Guard("poll", async () =>
            {
                if (!state.Running || IsYouTubePaused()) return;

                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var steps = scope.ServiceProvider.GetRequiredService<PipelineSteps>();
                var due = DateTime.UtcNow.AddMinutes(-_options.PollMinutes);
                var channels = await db.Channels.Where(c => c.LastPolledAt == null || c.LastPolledAt < due).ToListAsync(run.Token);

                foreach (var channel in channels)
                {
                    await steps.PollChannelAsync(channel, run.Token);
                    await Announce(channel.Id, ct);
                }
            }, ct);
            await WaitAsync(TimeSpan.FromSeconds(state.Running ? 30 : 2), run.Token, ct);
        }
    }

    /// <summary>One video at a time with a randomized gap. This is the step that gets IPs blocked if rushed.</summary>
    private async Task TranscriptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var worked = false;
            using var run = RunScope(ct);
            await Guard("transcript", async () =>
            {
                if (!state.Running || IsYouTubePaused()) return;

                await using var scope = scopes.CreateAsyncScope();
                var channel = await scope.ServiceProvider.GetRequiredService<PipelineSteps>().FetchNextTranscriptAsync(run.Token);
                if (channel is { } id)
                {
                    worked = true;
                    await Announce(id, ct);
                }
            }, ct);

            var delay = worked
                ? TimeSpan.FromSeconds(Random.Shared.Next(_options.MinFetchDelaySeconds, _options.MaxFetchDelaySeconds + 1))
                : state.Running ? IdleDelay : TimeSpan.FromSeconds(2);
            await WaitAsync(delay, run.Token, ct);
        }
    }

    private async Task ExtractLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var worked = false;
            using var run = RunScope(ct);
            await Guard("extraction", async () =>
            {
                // With no key, videos simply wait at TranscriptFetched and carry on once one is set.
                if (!state.Running || !llm.IsConfigured || IsLlmPaused()) return;

                await using var scope = scopes.CreateAsyncScope();
                var channel = await scope.ServiceProvider.GetRequiredService<PipelineSteps>().ExtractNextAsync(run.Token);
                if (channel is { } id)
                {
                    worked = true;
                    state.LlmPausedUntil = null;
                    state.LlmPauseReason = null;
                    await Announce(id, ct);
                }
            }, ct);
            if (!worked) await WaitAsync(state.Running ? IdleDelay : TimeSpan.FromSeconds(2), run.Token, ct);
        }
    }

    private async Task ScoreLoopAsync(CancellationToken ct)
    {
        var lastFullRun = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            await Guard("scoring", async () =>
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Newly extracted videos are scored right away. Everything else needs only a daily close.
                var waiting = await db.Videos.AnyAsync(v => v.Status == Domain.VideoStatus.Extracted, ct);
                if (!waiting && DateTime.UtcNow - lastFullRun < TimeSpan.FromHours(6)) return;

                var changed = await scope.ServiceProvider.GetRequiredService<ScoringService>().RunAsync(ct);
                lastFullRun = DateTime.UtcNow;
                foreach (var id in changed) await Announce(id, ct);
            }, ct);
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Guard("maintenance", async () =>
            {
                await using var scope = scopes.CreateAsyncScope();
                var removed = await scope.ServiceProvider.GetRequiredService<PipelineSteps>().PruneTranscriptsAsync(ct);
                if (removed > 0) log.LogInformation("Pruned {Count} old transcripts", removed);

                // Waits until the queue is clear, so a backfill in progress doesn't produce a brief from partial data.
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var busy = await db.Videos.AnyAsync(v =>
                    v.Status == Domain.VideoStatus.Discovered || v.Status == Domain.VideoStatus.TranscriptFetched || v.Status == Domain.VideoStatus.Extracted, ct);
                if (!busy && state.Running && llm.IsConfigured && !IsLlmPaused())
                    await scope.ServiceProvider.GetRequiredService<DigestService>().WriteDailyBriefAsync(ct);
            }, ct);
            await Task.Delay(TimeSpan.FromMinutes(_options.MaintenanceMinutes), ct);
        }
    }

    /// <summary>A token that ends on shutdown or when the pipeline is stopped.</summary>
    private CancellationTokenSource RunScope(CancellationToken stoppingToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, state.RunToken);

    /// <summary>Waits, returning early if the pipeline is stopped. Only shutdown ends the loop.</summary>
    private static async Task WaitAsync(TimeSpan delay, CancellationToken run, CancellationToken stoppingToken)
    {
        // When stopped, the run token is already cancelled, so wait on shutdown alone to avoid a busy loop.
        if (run.IsCancellationRequested) { await Task.Delay(delay, stoppingToken); return; }
        try { await Task.Delay(delay, run); }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
    }

    private bool IsYouTubePaused() => state.YouTubePausedUntil > DateTime.UtcNow;

    private bool IsLlmPaused() => state.LlmPausedUntil > DateTime.UtcNow;

    /// <summary>Keeps one failure from ending a loop, and turns refusals into a pause across that service.</summary>
    private async Task Guard(string name, Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work();
        }
        catch (YouTubeBlockedException ex)
        {
            // A refusal needs time to cool off. A dropped connection only needs a moment before trying again.
            var minutes = ex.WasRefused ? _options.BlockedPauseMinutes : _options.NetworkPauseMinutes;
            log.LogWarning("Pausing YouTube requests for {Minutes} minutes: {Message} ({Detail})", minutes, ex.Message, ex.InnerException?.GetBaseException().Message);
            state.YouTubePausedUntil = DateTime.UtcNow.AddMinutes(minutes);
            state.YouTubePauseReason = ex.Message;
        }
        catch (LlmUnavailableException ex)
        {
            log.LogWarning("Pausing the model: {Message}", ex.Message);
            state.LlmPausedUntil = DateTime.UtcNow + (ex.RetryAfter ?? TimeSpan.FromMinutes(ex.IsCredentialProblem ? 30 : 2));
            state.LlmPauseReason = ex.Message;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !state.Running)
        {
            log.LogInformation("Pipeline step {Step} stopped", name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(ex, "Pipeline step {Step} failed", name);
        }
    }
}
