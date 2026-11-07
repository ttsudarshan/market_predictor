using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Contracts;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;
using PaperTrading.Api.Insights;
using PaperTrading.Api.MarketData;
using static PaperTrading.Tests.Bars;

namespace PaperTrading.Tests;

public class YouTubeLinkTests
{
    [Theory]
    [InlineData("https://www.youtube.com/@MeetKevin", YouTubeLinkKind.Handle, "MeetKevin")]
    [InlineData("https://youtube.com/@MeetKevin/videos", YouTubeLinkKind.Handle, "MeetKevin")]
    [InlineData("youtube.com/@MeetKevin", YouTubeLinkKind.Handle, "MeetKevin")]
    [InlineData("@MeetKevin", YouTubeLinkKind.Handle, "MeetKevin")]
    [InlineData("MeetKevin", YouTubeLinkKind.Handle, "MeetKevin")]
    [InlineData("https://www.youtube.com/channel/UCrp_UI8XtuYfpiqluWLD7Lw", YouTubeLinkKind.ChannelId, "UCrp_UI8XtuYfpiqluWLD7Lw")]
    [InlineData("UCrp_UI8XtuYfpiqluWLD7Lw", YouTubeLinkKind.ChannelId, "UCrp_UI8XtuYfpiqluWLD7Lw")]
    [InlineData("https://www.youtube.com/c/SomeSlug", YouTubeLinkKind.Slug, "SomeSlug")]
    [InlineData("https://www.youtube.com/user/SomeUser", YouTubeLinkKind.User, "SomeUser")]
    [InlineData("https://www.youtube.com/watch?v=iCX0gGvm45s", YouTubeLinkKind.Video, "iCX0gGvm45s")]
    [InlineData("https://youtu.be/iCX0gGvm45s", YouTubeLinkKind.Video, "iCX0gGvm45s")]
    [InlineData("https://www.youtube.com/shorts/iCX0gGvm45s", YouTubeLinkKind.Video, "iCX0gGvm45s")]
    public void Recognizes_every_kind_of_pasted_link(string input, YouTubeLinkKind kind, string value)
    {
        var parsed = YouTubeLinks.Parse(input);

        Assert.Equal(kind, parsed?.Kind);
        Assert.Equal(value, parsed?.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://evil.example.com/@MeetKevin")]
    [InlineData("https://youtube.com.evil.example/channel/UCrp_UI8XtuYfpiqluWLD7Lw")]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/feed/subscriptions")]
    public void Refuses_anything_that_is_not_a_youtube_channel_or_video(string input) =>
        Assert.Null(YouTubeLinks.Parse(input));

    [Fact]
    public void Transcript_lines_group_captions_and_keep_timestamps()
    {
        var text = YouTubeGateway.Format([
            (TimeSpan.FromSeconds(1), "nvidia is"),
            (TimeSpan.FromSeconds(5), "going\nhigher"),
            (TimeSpan.FromSeconds(30), "next point"),
            (TimeSpan.FromSeconds(3725), "much later"),
        ]);

        Assert.Equal(
            "[00:01] nvidia is going higher\n[00:30] next point\n[1:02:05] much later\n",
            text.Replace("\r\n", "\n"));
    }
}

public class YouTubeFailureTests
{
    private sealed class Unavailable : Exception;

    private static Task<int> Run(Exception failure) =>
        YouTubeGateway.Guarded<int>(() => throw failure, () => new Unavailable());

    [Fact]
    public Task A_missing_page_means_the_item_is_unavailable() =>
        Assert.ThrowsAsync<Unavailable>(() => Run(new HttpRequestException("x", null, HttpStatusCode.NotFound)));

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Being_refused_needs_the_long_pause(HttpStatusCode status)
    {
        var ex = await Assert.ThrowsAsync<YouTubeBlockedException>(() => Run(new HttpRequestException("x", null, status)));
        Assert.True(ex.WasRefused);
    }

    [Fact]
    public async Task A_dropped_connection_only_needs_the_short_pause()
    {
        var ex = await Assert.ThrowsAsync<YouTubeBlockedException>(() => Run(new HttpRequestException("network down")));
        Assert.False(ex.WasRefused);
    }

    [Fact]
    public async Task An_http_timeout_is_recognized_rather_than_treated_as_an_unexpected_error()
    {
        // This is the shape HttpClient throws when its own timeout elapses, with its internal token cancelled.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var timeout = new TaskCanceledException("Timed out.", new TimeoutException(), cts.Token);

        var ex = await Assert.ThrowsAsync<YouTubeBlockedException>(() => Run(timeout));
        Assert.False(ex.WasRefused);
    }

    [Fact]
    public Task Shutting_down_passes_through_as_a_cancellation() =>
        Assert.ThrowsAsync<TaskCanceledException>(() => Run(new TaskCanceledException()));
}

public class ClaimParsingTests
{
    private const string Transcript = "[00:10] I think Nvidia, honestly, goes to $200 by March.\n[00:40] Tesla looks weak here\n[1:05:00] and that is all";

    private static string Json(string ticker = "NVDA", string direction = "bullish", string quote = "Nvidia, honestly, goes to $200 by March", string target = "200", string horizon = "90") =>
        $$"""{"claims":[{"ticker":"{{ticker}}","direction":"{{direction}}","target_price":{{target}},"horizon_days":{{horizon}},"confidence":"high","quote":"{{quote}}","timestamp_seconds":10,"reasoning":"AI demand","speaker":null}]}""";

    [Fact]
    public void Reads_a_well_formed_call_and_confirms_its_quote()
    {
        var claim = Assert.Single(ClaimExtractor.Parse(Json(), Transcript));

        Assert.Equal("NVDA", claim.Ticker);
        Assert.Equal(ClaimDirection.Bullish, claim.Direction);
        Assert.Equal(200m, claim.TargetPrice);
        Assert.Equal(90, claim.HorizonDays);
        Assert.Equal(ClaimConfidence.High, claim.Confidence);
        Assert.True(claim.QuoteVerified);
    }

    [Theory]
    [InlineData("NVIDIA, HONESTLY GOES TO 200 BY MARCH")]           // casing and punctuation differ from the captions
    [InlineData("goes to $200 by March. Tesla looks weak")]         // runs across a [mm:ss] marker
    public void Quote_check_ignores_formatting_differences(string quote) =>
        Assert.True(Assert.Single(ClaimExtractor.Parse(Json(quote: quote), Transcript)).QuoteVerified);

    [Theory]
    [InlineData("Apple will triple next year")]                     // never said
    [InlineData("goes to 500 by March")]                            // a changed number
    public void A_quote_the_speaker_never_said_is_flagged(string quote) =>
        Assert.False(Assert.Single(ClaimExtractor.Parse(Json(quote: quote), Transcript)).QuoteVerified);

    [Theory]
    [InlineData("../etc/passwd", "bullish")]
    [InlineData("NVDA", "sideways")]
    public void Malformed_entries_are_dropped(string ticker, string direction) =>
        Assert.Empty(ClaimExtractor.Parse(Json(ticker: ticker, direction: direction), Transcript));

    [Fact]
    public void Repeat_calls_within_a_video_become_one()
    {
        var one = Json()[11..^2];
        Assert.Single(ClaimExtractor.Parse($$"""{"claims":[{{one}},{{one}}]}""", Transcript));
    }

    [Fact]
    public void An_empty_list_is_a_normal_result()
    {
        Assert.Empty(ClaimExtractor.Parse("""{"claims":[]}""", Transcript));
    }

    [Fact]
    public void Broken_json_is_reported_rather_than_read_as_no_calls()
    {
        Assert.Throws<LlmRejectedException>(() => ClaimExtractor.Parse("not json", Transcript));
    }
}

public sealed class PipelineHarness : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public FakeYouTube YouTube { get; } = new();
    public FakeLlm Llm { get; } = new();
    public FakePrices Prices { get; } = new();
    public AppDbContext Db { get; }
    public PipelineSteps Steps { get; }
    public ScoringService Scoring { get; }
    public Channel Channel { get; }

    public PipelineHarness(string? whisper = null)
    {
        _connection.Open();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        Db.Database.EnsureCreated();

        var options = Options.Create(new InsightsOptions { WhisperCommand = whisper, MaxTranscriptAttempts = 3, MaxExtractionAttempts = 2 });
        Steps = new PipelineSteps(Db, YouTube, new ClaimExtractor(Llm), Llm, Prices, options, NullLogger<PipelineSteps>.Instance);
        Scoring = new ScoringService(Db, Prices, options, NullLogger<ScoringService>.Instance);

        Channel = new Channel { YouTubeChannelId = "UC1", Title = "Test Channel", BackfillCount = 60 };
        Db.Channels.Add(Channel);
        Db.SaveChanges();
    }

    public void AddVideo(string id, string published, string? captions = "[00:05] nvidia goes higher from here")
    {
        YouTube.Uploads.Add(new UploadInfo(id, $"Video {id}", 600));
        YouTube.Published[id] = Utc(published);
        if (captions is not null) YouTube.Captions[id] = new TranscriptData(captions, "en", "captions");
    }

    public Video Get(string id)
    {
        Db.ChangeTracker.Clear();
        return Db.Videos.AsNoTracking().Single(v => v.YouTubeVideoId == id);
    }

    public void MakeDue()
    {
        Db.Videos.ExecuteUpdate(s => s.SetProperty(v => v.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
        Db.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}

public class PipelineStepTests
{
    private const string NvdaCall = """{"claims":[{"ticker":"NVDA","direction":"bullish","target_price":null,"horizon_days":null,"confidence":"high","quote":"nvidia goes higher from here","timestamp_seconds":5,"reasoning":null,"speaker":null}]}""";

    private static void PricesFor(PipelineHarness h, string symbol) =>
        h.Prices.Bars[symbol] = [On("2026-01-05", 100), On("2026-01-06", 110), On("2026-01-07", 121)];

    [Fact]
    public async Task A_video_moves_through_every_status_to_scored()
    {
        using var h = new PipelineHarness();
        h.AddVideo("v1", "2026-01-05T12:00:00");
        h.Llm.Handler = (_, _) => new LlmResult(NvdaCall, 5000, 200);
        PricesFor(h, "NVDA");
        PricesFor(h, "SPY");

        Assert.Equal(1, await h.Steps.DiscoverAsync(h.Channel, 60, default));
        Assert.Equal(VideoStatus.Discovered, h.Get("v1").Status);

        await h.Steps.FetchNextTranscriptAsync(default);
        Assert.Equal(VideoStatus.TranscriptFetched, h.Get("v1").Status);
        Assert.Equal(Utc("2026-01-05T12:00:00"), h.Get("v1").PublishedAt);

        await h.Steps.ExtractNextAsync(default);
        var video = h.Get("v1");
        Assert.Equal(VideoStatus.Extracted, video.Status);
        Assert.Equal(5000, video.InputTokens);
        Assert.Equal("fake-model", video.ExtractionModel);
        Assert.Equal(ClaimExtractor.PromptVersion, video.PromptVersion);

        var claim = await h.Db.Claims.AsNoTracking().SingleAsync();
        Assert.Equal("NVDA", claim.Ticker);
        Assert.False(claim.HorizonStated);
        Assert.Equal(30, claim.HorizonDays);
        Assert.Equal(ClaimConfidence.Low, claim.Confidence);        // vague, however firmly it was said
        Assert.Equal(Utc("2026-02-04T12:00:00"), claim.ResolvesAt);
        Assert.True(claim.QuoteVerified);

        await h.Scoring.RunAsync();
        Assert.Equal(VideoStatus.Scored, h.Get("v1").Status);
        var scored = await h.Db.Claims.AsNoTracking().SingleAsync();
        Assert.Equal(100m, scored.EntryPrice);
        Assert.Equal(ClaimStatus.Unscorable, scored.Status);        // its date passed long ago with no price on or after it
    }

    [Fact]
    public async Task Running_discovery_again_never_duplicates_a_video()
    {
        using var h = new PipelineHarness();
        h.AddVideo("v1", "2026-01-05T12:00:00");

        await h.Steps.DiscoverAsync(h.Channel, 60, default);
        Assert.Equal(0, await h.Steps.DiscoverAsync(h.Channel, 60, default));

        Assert.Single(h.Db.Videos);
    }

    [Fact]
    public async Task Discovery_only_takes_the_requested_number_of_uploads()
    {
        using var h = new PipelineHarness();
        for (var i = 0; i < 5; i++) h.AddVideo($"v{i}", "2026-01-05T12:00:00");

        Assert.Equal(3, await h.Steps.DiscoverAsync(h.Channel, 3, default));
    }

    [Fact]
    public async Task A_channel_whose_first_check_failed_still_gets_its_full_backfill_later()
    {
        using var h = new PipelineHarness();
        for (var i = 0; i < 40; i++) h.AddVideo($"v{i}", "2026-01-05T12:00:00");
        h.YouTube.UploadsFailure = new TradingException("That channel's uploads aren't available.");

        Assert.Equal(0, await h.Steps.PollChannelAsync(h.Channel, default));
        Assert.Contains("aren't available", h.Channel.LastPollError);
        Assert.NotNull(h.Channel.LastPolledAt);

        h.YouTube.UploadsFailure = null;
        Assert.Equal(40, await h.Steps.PollChannelAsync(h.Channel, default));
        Assert.Null(h.Channel.LastPollError);

        // Once it has videos, later checks only look at the newest few.
        h.YouTube.Uploads.Insert(0, new UploadInfo("new", "New", 60));
        h.YouTube.Published["new"] = Utc("2026-02-01T00:00:00");
        h.YouTube.LastRequestedCount = 0;
        Assert.Equal(1, await h.Steps.PollChannelAsync(h.Channel, default));
        Assert.Equal(15, h.YouTube.LastRequestedCount);
    }

    [Fact]
    public async Task Missing_captions_retry_later_then_give_up_and_can_be_requeued()
    {
        using var h = new PipelineHarness();
        h.AddVideo("v1", "2026-01-05T12:00:00", captions: null);
        await h.Steps.DiscoverAsync(h.Channel, 60, default);

        await h.Steps.FetchNextTranscriptAsync(default);
        var first = h.Get("v1");
        Assert.Equal(VideoStatus.Discovered, first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.True(first.NextAttemptAt > DateTime.UtcNow.AddMinutes(30));

        // Not due yet, so nothing is requested.
        Assert.Null(await h.Steps.FetchNextTranscriptAsync(default));
        Assert.Equal(1, h.YouTube.CaptionCalls);

        h.MakeDue();
        await h.Steps.FetchNextTranscriptAsync(default);
        h.MakeDue();
        await h.Steps.FetchNextTranscriptAsync(default);
        Assert.Equal(VideoStatus.NoCaptions, h.Get("v1").Status);

        h.YouTube.Captions["v1"] = new TranscriptData("[00:01] hi", "en", "captions");
        var channels = new ChannelService(h.Db, h.YouTube, Options.Create(new InsightsOptions()));
        Assert.Equal(1, await channels.RetryAsync(h.Channel.Id));
        await h.Steps.FetchNextTranscriptAsync(default);
        Assert.Equal(VideoStatus.TranscriptFetched, h.Get("v1").Status);
    }

    [Fact]
    public async Task A_youtube_block_propagates_and_leaves_the_video_waiting()
    {
        using var h = new PipelineHarness();
        h.AddVideo("v1", "2026-01-05T12:00:00");
        await h.Steps.DiscoverAsync(h.Channel, 60, default);
        h.YouTube.Blocked = true;

        await Assert.ThrowsAsync<YouTubeBlockedException>(() => h.Steps.FetchNextTranscriptAsync(default));

        var video = h.Get("v1");
        Assert.Equal(VideoStatus.Discovered, video.Status);
        Assert.Equal(0, video.Attempts);
    }

    [Fact]
    public async Task A_video_that_keeps_failing_cant_hold_up_the_rest_of_the_queue()
    {
        using var h = new PipelineHarness();
        h.AddVideo("bad", "2026-01-05T12:00:00");
        h.AddVideo("good", "2026-01-05T12:00:00");
        await h.Steps.DiscoverAsync(h.Channel, 60, default);
        h.YouTube.BlockedVideos.Add("bad");

        for (var i = 0; i < 2; i++)
        {
            try { await h.Steps.FetchNextTranscriptAsync(default); }
            catch (YouTubeBlockedException) { }
        }

        Assert.Equal(VideoStatus.TranscriptFetched, h.Get("good").Status);
        Assert.Equal(VideoStatus.Discovered, h.Get("bad").Status);
        Assert.Equal(0, h.Get("bad").Attempts);
        Assert.NotNull(h.Get("bad").LastError);
    }

    [Fact]
    public async Task A_video_no_longer_available_ends_as_failed_not_stuck()
    {
        using var h = new PipelineHarness();
        h.AddVideo("v1", "2026-01-05T12:00:00");
        await h.Steps.DiscoverAsync(h.Channel, 60, default);
        h.YouTube.Published.Clear();

        for (var i = 0; i < 3; i++)
        {
            await h.Steps.FetchNextTranscriptAsync(default);
            h.MakeDue();
        }

        Assert.Equal(VideoStatus.Failed, h.Get("v1").Status);
    }

    [Fact]
    public async Task A_transcript_goes_to_the_model_once_and_a_finished_video_is_never_resent()
    {
        using var h = new PipelineHarness();
        h.AddVideo("v1", "2026-01-05T12:00:00");
        await h.Steps.DiscoverAsync(h.Channel, 60, default);
        await h.Steps.FetchNextTranscriptAsync(default);

        await h.Steps.ExtractNextAsync(default);
        h.MakeDue();

        Assert.Null(await h.Steps.ExtractNextAsync(default));
        Assert.Equal(1, h.Llm.Calls);
    }

    [Fact]
    public async Task No_calls_in_a_video_is_a_normal_result_that_reaches_scored()
    {
        using var h = new PipelineHarness();
        h.AddVideo("v1", "2026-01-05T12:00:00");
        await h.Steps.DiscoverAsync(h.Channel, 60, default);
        await h.Steps.FetchNextTranscriptAsync(default);
        await h.Steps.ExtractNextAsync(default);

        await h.Scoring.RunAsync();

        Assert.Equal(VideoStatus.Scored, h.Get("v1").Status);
        Assert.Empty(h.Db.Claims);
    }

    [Fact]
    public async Task Made_up_tickers_are_dropped_and_counted()
    {
        using var h = new PipelineHarness();
        h.AddVideo("v1", "2026-01-05T12:00:00");
        h.Llm.Handler = (_, _) => new LlmResult(NvdaCall.Replace("NVDA", "NOTREAL"), 10, 10);
        await h.Steps.DiscoverAsync(h.Channel, 60, default);
        await h.Steps.FetchNextTranscriptAsync(default);

        await h.Steps.ExtractNextAsync(default);

        Assert.Empty(h.Db.Claims);
        Assert.Equal(1, h.Get("v1").RejectedClaims);
    }

    [Fact]
    public async Task A_price_outage_during_extraction_keeps_the_call_for_scoring_to_judge()
    {
        using var h = new PipelineHarness();
        h.AddVideo("v1", "2026-01-05T12:00:00");
        h.Llm.Handler = (_, _) => new LlmResult(NvdaCall, 10, 10);
        await h.Steps.DiscoverAsync(h.Channel, 60, default);
        await h.Steps.FetchNextTranscriptAsync(default);
        h.Prices.Failing = true;

        await h.Steps.ExtractNextAsync(default);

        Assert.Single(h.Db.Claims);
    }

    [Fact]
    public async Task A_model_failure_keeps_the_transcript_and_a_later_retry_succeeds()
    {
        using var h = new PipelineHarness();
        h.AddVideo("v1", "2026-01-05T12:00:00");
        await h.Steps.DiscoverAsync(h.Channel, 60, default);
        await h.Steps.FetchNextTranscriptAsync(default);
        h.Llm.Handler = (_, _) => throw new LlmUnavailableException("Rate limited.");

        await Assert.ThrowsAsync<LlmUnavailableException>(() => h.Steps.ExtractNextAsync(default));

        Assert.Equal(VideoStatus.TranscriptFetched, h.Get("v1").Status);
        Assert.Single(h.Db.Transcripts);

        h.Llm.Handler = null;
        h.MakeDue();
        await h.Steps.ExtractNextAsync(default);
        Assert.Equal(VideoStatus.Extracted, h.Get("v1").Status);
    }

    [Fact]
    public async Task A_refused_transcript_fails_after_its_attempts_instead_of_costing_money_forever()
    {
        using var h = new PipelineHarness();
        h.AddVideo("v1", "2026-01-05T12:00:00");
        await h.Steps.DiscoverAsync(h.Channel, 60, default);
        await h.Steps.FetchNextTranscriptAsync(default);
        h.Llm.Handler = (_, _) => throw new LlmRejectedException("The model declined to process this.");

        await h.Steps.ExtractNextAsync(default);
        h.MakeDue();
        await h.Steps.ExtractNextAsync(default);

        Assert.Equal(VideoStatus.Failed, h.Get("v1").Status);
        Assert.Equal(2, h.Llm.Calls);
    }

    [Fact]
    public async Task A_response_cut_off_for_length_fails_at_once_instead_of_paying_to_repeat_it()
    {
        using var h = new PipelineHarness();
        h.AddVideo("v1", "2026-01-05T12:00:00");
        await h.Steps.DiscoverAsync(h.Channel, 60, default);
        await h.Steps.FetchNextTranscriptAsync(default);
        h.Llm.Handler = (_, _) => throw new LlmRejectedException("The response was too long and was cut off.", retryable: false);

        await h.Steps.ExtractNextAsync(default);

        Assert.Equal(VideoStatus.Failed, h.Get("v1").Status);
        Assert.Equal(1, h.Llm.Calls);
    }

    [Fact]
    public async Task Pruning_removes_old_transcripts_but_keeps_claims_and_waiting_work()
    {
        using var h = new PipelineHarness();
        h.AddVideo("done", "2026-01-05T12:00:00");
        h.AddVideo("waiting", "2026-01-05T12:00:00");
        h.Llm.Handler = (_, user) => new LlmResult(NvdaCall, 10, 10);
        PricesFor(h, "NVDA");
        await h.Steps.DiscoverAsync(h.Channel, 60, default);
        await h.Steps.FetchNextTranscriptAsync(default);
        await h.Steps.FetchNextTranscriptAsync(default);

        var first = h.Db.Videos.AsNoTracking().OrderByDescending(v => v.PublishedAt).ThenBy(v => v.Id).First().Id;
        await h.Steps.ExtractNextAsync(default);
        h.Db.Transcripts.ExecuteUpdate(s => s.SetProperty(t => t.FetchedAt, DateTime.UtcNow.AddDays(-120)));
        h.Db.ChangeTracker.Clear();

        Assert.Equal(1, await h.Steps.PruneTranscriptsAsync(default));

        Assert.Single(h.Db.Transcripts);
        Assert.Single(h.Db.Claims);
    }

    [Fact]
    public async Task Scoring_real_flow_compares_against_stored_benchmark_prices()
    {
        using var h = new PipelineHarness();
        var published = DateTime.UtcNow.AddDays(-10);
        var start = DateOnly.FromDateTime(published);
        DailyBar Bar(int day, decimal close) => On(start.AddDays(day).ToString("yyyy-MM-dd"), close);

        h.Prices.Bars["NVDA"] = [Bar(0, 100), Bar(1, 100), Bar(2, 110), Bar(3, 120), Bar(4, 130)];
        h.Prices.Bars["SPY"] = [Bar(0, 500), Bar(1, 500), Bar(2, 505), Bar(3, 505), Bar(4, 510)];
        h.Db.Claims.Add(new Claim
        {
            ChannelId = h.Channel.Id, VideoId = (await AddVideoRow(h, VideoStatus.Extracted)).Id, Ticker = "NVDA", Quote = "q",
            Direction = ClaimDirection.Bullish, HorizonDays = 3, HorizonStated = true, QuoteVerified = true,
            MadeAt = published, ResolvesAt = published.AddDays(3),
        });
        await h.Db.SaveChangesAsync();

        await h.Scoring.RunAsync();
        var claim = await h.Db.Claims.AsNoTracking().SingleAsync();
        Assert.Equal(ClaimStatus.Final, claim.Status);
        Assert.NotNull(claim.ExcessReturnPercent);
        Assert.Equal(5, h.Db.PriceBars.Count(b => b.Symbol == "NVDA"));
    }

    private static async Task<Video> AddVideoRow(PipelineHarness h, VideoStatus status)
    {
        var video = new Video { ChannelId = h.Channel.Id, YouTubeVideoId = Guid.NewGuid().ToString("N")[..11], Title = "t", Status = status };
        h.Db.Videos.Add(video);
        await h.Db.SaveChangesAsync();
        return video;
    }
}

public class WhisperCommandTests
{
    [Fact]
    public async Task The_speech_to_text_command_gets_the_file_and_its_output_becomes_a_timestamped_transcript()
    {
        var script = Path.Combine(Path.GetTempPath(), $"whisper-{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(script, "import json,sys\nprint(json.dumps([{'start':65.5,'text':'heard '+sys.argv[1]}]))\n");
        try
        {
            var transcript = await PipelineSteps.RunWhisperAsync($"python3 -I {script} {{audio}}", "file.m4a", default);

            Assert.Contains("[01:05] heard file.m4a", transcript.Content);
            Assert.Equal("speech-to-text", transcript.Source);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task A_failing_speech_to_text_command_reports_why()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PipelineSteps.RunWhisperAsync("python3 -I -c raise(SystemExit(3)) {audio}", "file.m4a", default));

        Assert.Contains("code 3", ex.Message);
    }
}

public class DatabaseUpgradeTests
{
    [Fact]
    public void A_database_from_before_migrations_upgrades_with_its_accounts_and_lists_intact_and_is_backed_up()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pt-upgrade-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(directory, "papertrading.db")}").Options;
        var userId = Guid.NewGuid();

        try
        {
            // Builds the old schema from the first migration, and removes the record of it, as EnsureCreated left things.
            using (var old = new AppDbContext(options))
            {
                old.GetInfrastructure().GetRequiredService<IMigrator>().Migrate("20261102014827_InitialCreate");
                old.Database.ExecuteSqlRaw("""DROP TABLE "__EFMigrationsHistory";""");
                old.Database.ExecuteSqlInterpolated($"""
                    INSERT INTO "Users" ("Id","Email","DisplayName","PasswordHash","Cash","StartingBalance","CreatedAt")
                    VALUES ({userId},'old@example.com','Old','hash','1234.5','100000','2026-01-01 00:00:00');
                    """);
                old.Database.ExecuteSqlInterpolated($"""INSERT INTO "Watchlist" ("UserId","Symbol","AddedAt") VALUES ({userId},'AAPL','2026-01-01 00:00:00');""");
                old.Database.ExecuteSqlInterpolated($"""
                    INSERT INTO "Orders" ("Id","UserId","Symbol","Side","Type","Quantity","Status","CreatedAt")
                    VALUES ({Guid.NewGuid()},{userId},'AAPL','Buy','Market','1','Filled','2026-01-01 00:00:00');
                    """);
            }

            using (var db = new AppDbContext(options))
            {
                DatabaseInitializer.Migrate(db, directory, NullLogger.Instance);

                Assert.Empty(db.Database.GetPendingMigrations());
                var user = db.Users.Single();
                Assert.Equal(1234.5m, user.Cash);

                var list = db.WatchLists.Single();
                Assert.Equal(userId, list.Id);
                Assert.True(list.IsDefault);
                Assert.Equal("Favorites", list.Name);
                Assert.Equal("AAPL", db.WatchlistItems.Single(i => i.WatchListId == list.Id).Symbol);
                Assert.Null(db.Orders.Single().ClaimId);
                Assert.Empty(db.Channels);
            }

            var backup = Assert.Single(Directory.GetFiles(directory, "papertrading.backup-*.db"));
            using var check = new SqliteConnection($"Data Source={backup}");
            check.Open();
            using var count = check.CreateCommand();
            count.CommandText = """SELECT COUNT(*) FROM "Watchlist";""";
            Assert.Equal(1L, count.ExecuteScalar());

            // A second start does nothing and makes no new backup.
            using (var again = new AppDbContext(options)) DatabaseInitializer.Migrate(again, directory, NullLogger.Instance);
            Assert.Single(Directory.GetFiles(directory, "papertrading.backup-*.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_new_database_starts_on_the_latest_schema_with_no_backup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pt-new-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(directory, "papertrading.db")}").Options;
        try
        {
            using var db = new AppDbContext(options);
            DatabaseInitializer.Migrate(db, directory, NullLogger.Instance);

            Assert.Empty(db.Database.GetPendingMigrations());
            Assert.Empty(Directory.GetFiles(directory, "papertrading.backup-*.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

public class InsightsApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> SignUp(string email = "a@example.com")
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "correct-horse", "Test"));
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);
        return client;
    }

    private async Task<(Video Video, Channel Channel)> SeedAsync(string ticker, ClaimDirection direction = ClaimDirection.Bullish)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var channel = new Channel { YouTubeChannelId = "UC" + Guid.NewGuid().ToString("N")[..10], Title = "Creator " + ticker, LastPolledAt = DateTime.UtcNow };
        var video = new Video { ChannelId = channel.Id, YouTubeVideoId = Guid.NewGuid().ToString("N")[..11], Title = "T", PublishedAt = DateTime.UtcNow.AddDays(-1), Status = VideoStatus.Scored };
        db.Channels.Add(channel);
        db.Videos.Add(video);
        db.Claims.Add(new Claim
        {
            ChannelId = channel.Id, VideoId = video.Id, Ticker = ticker, Direction = direction, Quote = "the quote", QuoteVerified = true,
            MadeAt = DateTime.UtcNow.AddDays(-1), ResolvesAt = DateTime.UtcNow.AddDays(29), HorizonDays = 30,
        });
        await db.SaveChangesAsync();
        return (video, channel);
    }

    [Fact]
    public async Task Insights_routes_require_a_token()
    {
        var client = _factory.CreateClient();
        foreach (var url in new[] { "/api/channels", "/api/claims", "/api/insights/leaderboard", "/api/insights/heads-up", "/api/insights/inbox", "/api/insights/status", "/api/watchlists" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Pasting_a_link_follows_the_channel_and_the_worker_discovers_its_videos()
    {
        var client = await SignUp();
        _factory.YouTube.Channels["youtube.com/@x"] = new ChannelInfo("UCx", "Channel X");
        _factory.YouTube.Uploads.Add(new UploadInfo("vid00000001", "A video", 300));

        var added = await client.PostAsJsonAsync("/api/channels", new { link = "youtube.com/@x", backfill = 30 });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var channel = await added.Content.ReadFromJsonAsync<ChannelDto>(Json);
        Assert.True(channel!.Following);

        // Adding the same channel again reuses it.
        await client.PostAsJsonAsync("/api/channels", new { link = "youtube.com/@x" });
        var list = await client.GetFromJsonAsync<ChannelDto[]>("/api/channels", Json);
        Assert.Single(list!);

        // Another account sees it on the leaderboard but doesn't follow it.
        var other = await SignUp("b@example.com");
        Assert.Empty((await other.GetFromJsonAsync<ChannelDto[]>("/api/channels", Json))!);
        Assert.Single((await other.GetFromJsonAsync<CreatorStats[]>("/api/insights/leaderboard", Json))!);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/channels/{channel.Id}/follow")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ChannelDto[]>("/api/channels", Json))!);
    }

    [Fact]
    public async Task A_bad_link_is_a_readable_error()
    {
        var client = await SignUp();
        var response = await client.PostAsJsonAsync("/api/channels", new { link = "not a channel" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("couldn't find", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Status_reports_a_missing_key_and_asking_explains_it()
    {
        var client = await SignUp();
        _factory.Llm.IsConfigured = false;

        var status = await client.GetFromJsonAsync<PipelineStatusDto>("/api/insights/status", Json);
        Assert.False(status!.LlmConfigured);

        var ask = await client.PostAsJsonAsync("/api/insights/ask", new { question = "Who is right on NVDA?" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ask.StatusCode);
        Assert.Contains("API key", await ask.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Asking_sends_only_relevant_claims_and_ignores_made_up_citations()
    {
        var client = await SignUp();
        await SeedAsync("NVDA");
        await SeedAsync("TSLA");
        _factory.Llm.Handler = (_, _) => new LlmResult("""{"answer":"One creator is bullish on NVDA.","cited_claims":["c0","c99","nonsense"]}""", 10, 10);

        var response = await client.PostAsJsonAsync("/api/insights/ask", new { question = "What are people saying about NVDA?" });
        var body = await response.Content.ReadFromJsonAsync<AskResponse>(Json);

        Assert.Equal("One creator is bullish on NVDA.", body!.Answer);
        Assert.Equal("NVDA", Assert.Single(body.Citations).Ticker);
        Assert.Contains("not financial advice", body.Disclaimer);
        Assert.DoesNotContain("\"ticker\":\"TSLA\"", _factory.Llm.LastUserMessage);
    }

    [Fact]
    public async Task Heads_up_and_inbox_show_calls_with_a_clear_label_and_reasons_to_watch()
    {
        var client = await SignUp();
        var (_, channel) = await SeedAsync("NVDA");
        await client.PostAsync($"/api/channels/{channel.Id}/follow", null);

        var headsUp = await client.GetFromJsonAsync<HeadsUpDto>("/api/insights/heads-up", Json);
        Assert.Contains("not a forecast", headsUp!.Disclaimer);
        Assert.Equal("NVDA", Assert.Single(headsUp.Tickers).Ticker);
        Assert.Equal(1m, headsUp.Tickers[0].NetSentiment);

        var main = (await client.GetFromJsonAsync<WatchListDto[]>("/api/watchlists", Json))![0];
        Assert.Contains("NVDA", main.Symbols);

        var inbox = await client.GetFromJsonAsync<InboxDayDto[]>("/api/insights/inbox", Json);
        var item = Assert.Single(Assert.Single(inbox!).Items);
        Assert.Contains("On your lists", item.Reasons);

        var other = await SignUp("c@example.com");
        Assert.Empty((await other.GetFromJsonAsync<InboxDayDto[]>("/api/insights/inbox", Json))!);
    }

    [Fact]
    public async Task A_top_rank_only_counts_as_a_reason_when_the_creator_beats_the_market()
    {
        var client = await SignUp();
        var (_, channel) = await SeedAsync("NVDA");
        await client.PostAsync($"/api/channels/{channel.Id}/follow", null);

        using (var scope = _factory.Services.CreateScope())
        {
            // Ten finished calls, all behind the market. The minimum to rank, and a #1 rank by default.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var video = await db.Videos.FirstAsync(v => v.ChannelId == channel.Id);
            for (var i = 0; i < 10; i++)
            {
                db.Claims.Add(new Claim
                {
                    ChannelId = channel.Id, VideoId = video.Id, Ticker = $"T{i}", Direction = ClaimDirection.Bullish, Quote = "q", QuoteVerified = true,
                    Status = ClaimStatus.Final, ReturnPercent = -5, ExcessReturnPercent = -5, HorizonStated = true,
                    MadeAt = DateTime.UtcNow.AddDays(-100), ResolvesAt = DateTime.UtcNow.AddDays(-70),
                });
            }
            await db.SaveChangesAsync();
        }

        var board = await client.GetFromJsonAsync<CreatorStats[]>("/api/insights/leaderboard", Json);
        Assert.Equal(1, board![0].Rank);

        var inbox = await client.GetFromJsonAsync<InboxDayDto[]>("/api/insights/inbox", Json);
        Assert.DoesNotContain(inbox!.SelectMany(d => d.Items).SelectMany(i => i.Reasons), r => r.Contains("ranked"));
    }

    [Fact]
    public void Worth_watching_reasons_skip_flagged_calls_and_a_creators_own_reversal()
    {
        var me = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();
        ClaimDto Call(Guid channel, bool verified = true, ClaimReview? review = null) => new(
            Guid.NewGuid(), Guid.NewGuid(), channel, "C", "V", "abc", "NVDA", ClaimDirection.Bullish, 200m, 30, true, ClaimConfidence.High,
            "q", 0, null, null, verified, DateTime.UtcNow, DateTime.UtcNow, ClaimStatus.Pending, null, null, null, null, null, review);

        var ranks = new Dictionary<Guid, int> { [me] = 1 };
        var onlyMine = new Dictionary<(string, ClaimDirection), IReadOnlySet<Guid>>
        {
            [("NVDA", ClaimDirection.Bullish)] = new HashSet<Guid> { me },
            [("NVDA", ClaimDirection.Bearish)] = new HashSet<Guid> { me },
        };

        Assert.Empty(DigestService.WorthWatchingReasons(Call(me, verified: false), ranks, [], [], onlyMine));
        Assert.Empty(DigestService.WorthWatchingReasons(Call(me, review: ClaimReview.Incorrect), ranks, [], [], onlyMine));
        Assert.DoesNotContain("Creators disagree on this", DigestService.WorthWatchingReasons(Call(me), ranks, [], [], onlyMine));

        var split = new Dictionary<(string, ClaimDirection), IReadOnlySet<Guid>>
        {
            [("NVDA", ClaimDirection.Bullish)] = new HashSet<Guid> { me },
            [("NVDA", ClaimDirection.Bearish)] = new HashSet<Guid> { someoneElse },
        };
        Assert.Contains("Creators disagree on this", DigestService.WorthWatchingReasons(Call(me), ranks, [], [], split));
    }

    [Fact]
    public async Task A_review_marks_a_claim_and_feeds_the_accuracy_figure()
    {
        var client = await SignUp();
        await SeedAsync("NVDA");
        var claim = (await client.GetFromJsonAsync<ClaimDto[]>("/api/claims?ticker=nvda", Json))!.Single();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/claims/{claim.Id}/review", new { review = "Incorrect" })).StatusCode);

        var accuracy = await client.GetFromJsonAsync<AccuracyDto>("/api/insights/accuracy", Json);
        Assert.Equal(1, accuracy!.Reviewed);
        Assert.Equal(0m, accuracy.Accuracy);
    }

    [Fact]
    public async Task A_paper_trade_from_a_claim_is_linked_and_tracked_and_must_match_its_ticker()
    {
        var client = await SignUp();
        await SeedAsync("NVDA");
        _factory.Market.Set("NVDA", 100m);
        _factory.Market.Set("TSLA", 50m);
        var claim = (await client.GetFromJsonAsync<ClaimDto[]>("/api/claims", Json))!.Single();

        var mismatch = await client.PostAsJsonAsync("/api/orders", new { symbol = "TSLA", side = "Buy", type = "Market", quantity = 1, claimId = claim.Id });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);

        var placed = await client.PostAsJsonAsync("/api/orders", new { symbol = "NVDA", side = "Buy", type = "Market", quantity = 2, claimId = claim.Id });
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);

        _factory.Market.Set("NVDA", 110m);
        var trades = await client.GetFromJsonAsync<ClaimTradeDto[]>("/api/insights/claim-trades", Json);
        var trade = Assert.Single(trades!);
        Assert.Equal(claim.Id, trade.ClaimId);
        Assert.Equal(20m, trade.ProfitLoss);
        Assert.Equal(claim.ChannelTitle, trade.ChannelTitle);
    }
}
