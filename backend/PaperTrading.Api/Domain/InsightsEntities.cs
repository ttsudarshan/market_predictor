namespace PaperTrading.Api.Domain;

/// <summary>
/// Where a video is in the pipeline. Each step saves its result and moves the status in one write,
/// so a restart resumes where it left off and nothing is fetched or sent to the model twice.
/// </summary>
public enum VideoStatus { Discovered, TranscriptFetched, Extracted, Scored, NoCaptions, Failed }

public enum ClaimDirection { Bullish, Bearish }

public enum ClaimConfidence { Low, Medium, High }

/// <summary>Pending until the horizon passes, then Final. Unscorable means no usable prices exist.</summary>
public enum ClaimStatus { Pending, Final, Unscorable }

public enum ClaimReview { Correct, Incorrect }

/// <summary>A YouTube channel. Shared by all accounts, so each video is processed once.</summary>
public class Channel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string YouTubeChannelId { get; set; }
    public required string Title { get; set; }
    public int BackfillCount { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastPolledAt { get; set; }
    public string? LastPollError { get; set; }
}

public class ChannelFollow
{
    public Guid UserId { get; set; }
    public Guid ChannelId { get; set; }
    public DateTime FollowedAt { get; set; } = DateTime.UtcNow;
}

public class Video
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChannelId { get; set; }
    public required string YouTubeVideoId { get; set; }
    public required string Title { get; set; }
    public int? DurationSeconds { get; set; }

    /// <summary>Null until the transcript step looks it up: the uploads list doesn't include dates.</summary>
    public DateTime? PublishedAt { get; set; }
    public VideoStatus Status { get; set; } = VideoStatus.Discovered;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    public string? ExtractionModel { get; set; }
    public string? PromptVersion { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Calls the model returned for tickers that don't exist.</summary>
    public int RejectedClaims { get; set; }
}

/// <summary>Deleted after the retention period. Claims and their quotes are kept for good.</summary>
public class Transcript
{
    public Guid VideoId { get; set; }

    /// <summary>One line per few seconds of speech, each starting with a [mm:ss] or [h:mm:ss] marker.</summary>
    public required string Content { get; set; }
    public required string Language { get; set; }
    public required string Source { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One prediction made in a video, and how it turned out.</summary>
public class Claim
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VideoId { get; set; }
    public Guid ChannelId { get; set; }
    public required string Ticker { get; set; }
    public ClaimDirection Direction { get; set; }
    public decimal? TargetPrice { get; set; }

    /// <summary>The horizon used for scoring: the one stated, or the default for a vague call.</summary>
    public int HorizonDays { get; set; }
    public bool HorizonStated { get; set; }
    public ClaimConfidence Confidence { get; set; }
    public required string Quote { get; set; }
    public int TimestampSeconds { get; set; }
    public string? Reasoning { get; set; }
    public string? Speaker { get; set; }

    /// <summary>False when the model's quote isn't in the transcript. Such calls never count towards rankings.</summary>
    public bool QuoteVerified { get; set; }
    public DateTime MadeAt { get; set; }
    public DateTime ResolvesAt { get; set; }

    public ClaimStatus Status { get; set; } = ClaimStatus.Pending;
    public decimal? EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }
    public decimal? BenchmarkEntryPrice { get; set; }
    public decimal? BenchmarkExitPrice { get; set; }

    /// <summary>The price move as a percentage, positive when it went the way the call said.</summary>
    public decimal? ReturnPercent { get; set; }

    /// <summary><see cref="ReturnPercent"/> compared with SPY over the same dates.</summary>
    public decimal? ExcessReturnPercent { get; set; }
    public bool? TargetHit { get; set; }
    public DateTime? ScoredAt { get; set; }

    /// <summary>Set by a person checking the extraction. Incorrect claims stop counting.</summary>
    public ClaimReview? Review { get; set; }
}

/// <summary>Daily prices from the real market, whichever feed the trading screens use.</summary>
public class PriceBar
{
    public required string Symbol { get; set; }
    public DateOnly Date { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public DateTime ClosesAt { get; set; }
}

/// <summary>The written market heads-up for one day.</summary>
public class DailyBrief
{
    public DateOnly Date { get; set; }
    public required string Summary { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

public class WatchList
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Every account's default list shares its Id with the user, and can't be deleted.</summary>
    public bool IsDefault => Id == UserId;
}
