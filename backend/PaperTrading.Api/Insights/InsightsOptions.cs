namespace PaperTrading.Api.Insights;

public class InsightsOptions
{
    /// <summary>How many past videos to process when a channel is added.</summary>
    public int DefaultBackfill { get; set; } = 60;
    public int MinBackfill { get; set; } = 1;
    public int MaxBackfill { get; set; } = 200;

    /// <summary>When false, the pipeline waits for Start on the Creators page after every launch.</summary>
    public bool StartOnLaunch { get; set; }

    public int PollMinutes { get; set; } = 30;
    public int NewVideosPerPoll { get; set; } = 15;

    /// <summary>The gap between transcript requests. Going faster is what gets an IP blocked.</summary>
    public int MinFetchDelaySeconds { get; set; } = 3;
    public int MaxFetchDelaySeconds { get; set; } = 8;
    public int BlockedPauseMinutes { get; set; } = 30;
    public int NetworkPauseMinutes { get; set; } = 2;

    /// <summary>Captions often appear hours after upload, so a video is retried this many times.</summary>
    public int MaxTranscriptAttempts { get; set; } = 5;
    public int MaxExtractionAttempts { get; set; } = 4;

    public int ExtractionConcurrency { get; set; } = 2;
    public int RetentionDays { get; set; } = 90;
    public int MaintenanceMinutes { get; set; } = 10;

    /// <summary>The horizon given to a call that doesn't state one.</summary>
    public int DefaultHorizonDays { get; set; } = 30;

    /// <summary>A creator needs this many finished calls before being ranked.</summary>
    public int MinCallsToRank { get; set; } = 10;
    public int HeadsUpDays { get; set; } = 14;
    public string Benchmark { get; set; } = "SPY";

    /// <summary>
    /// Optional speech-to-text for videos with no captions. {audio} is replaced with the downloaded file,
    /// and the command must print a JSON array of {"start": seconds, "text": "..."}.
    /// </summary>
    public string? WhisperCommand { get; set; }
}

public class LlmOptions
{
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>low, medium, high, xhigh or max. Higher effort costs more.</summary>
    public string Effort { get; set; } = "high";

    /// <summary>Optional. Otherwise the SDK uses ANTHROPIC_API_KEY or an `ant auth login` profile.</summary>
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }

    /// <summary>Anthropic or Gemini. If empty, Gemini is used when only GEMINI_API_KEY is set, otherwise Anthropic.</summary>
    public string? Provider { get; set; }

    public string GeminiModel { get; set; } = "gemini-3.5-flash";

    /// <summary>Optional. Otherwise GEMINI_API_KEY is used.</summary>
    public string? GeminiApiKey { get; set; }

    public bool UsesGemini() => Provider?.Trim().ToLowerInvariant() switch
    {
        "gemini" => true,
        "anthropic" => false,
        _ => string.IsNullOrWhiteSpace(ApiKey) &&
             string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) &&
             !string.IsNullOrWhiteSpace(GeminiApiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")),
    };
}
