using System.ComponentModel.DataAnnotations;
using PaperTrading.Api.Domain;

namespace PaperTrading.Api.Insights;

public record AddChannelRequest([Required] string Link, int? Backfill);

public record VideoCounts(int Total, int Waiting, int TranscriptFetched, int Extracted, int Scored, int NoCaptions, int Failed);

public record ChannelDto(
    Guid Id, string YouTubeChannelId, string Title, DateTime AddedAt, DateTime? LastPolledAt, string? LastPollError,
    bool Following, VideoCounts Counts, CreatorStats Stats);

public record VideoDto(
    Guid Id, string YouTubeVideoId, string Title, DateTime? PublishedAt, VideoStatus Status,
    string? LastError, int ClaimCount, int RejectedClaims, DateTime? NextAttemptAt);

public record ClaimDto(
    Guid Id, Guid VideoId, Guid ChannelId, string ChannelTitle, string VideoTitle, string YouTubeVideoId,
    string Ticker, ClaimDirection Direction, decimal? TargetPrice, int HorizonDays, bool HorizonStated, ClaimConfidence Confidence,
    string Quote, int TimestampSeconds, string? Reasoning, string? Speaker, bool QuoteVerified,
    DateTime MadeAt, DateTime ResolvesAt, ClaimStatus Status, decimal? EntryPrice, decimal? ExitPrice,
    decimal? ReturnPercent, decimal? ExcessReturnPercent, bool? TargetHit, ClaimReview? Review);

public record ReversalDto(string Ticker, DateTime FirstAt, ClaimDirection FirstDirection, DateTime SecondAt, ClaimDirection SecondDirection, int DaysApart);

public record ChannelDetailDto(ChannelDto Channel, IReadOnlyList<VideoDto> Videos, IReadOnlyList<ClaimDto> Claims, IReadOnlyList<ReversalDto> Reversals);

public record ReviewRequest(ClaimReview? Review);

public record PipelineStatusDto(
    bool LlmConfigured, string Model, string? LlmPausedReason, DateTime? LlmPausedUntil,
    string? YouTubePausedReason, DateTime? YouTubePausedUntil, bool WhisperConfigured,
    int MinCallsToRank, int DefaultHorizonDays, string Benchmark, long InputTokens, long OutputTokens, bool Running);

public record AccuracyDto(int Reviewed, int Correct, int Incorrect, decimal? Accuracy, int TotalClaims, int UnverifiedQuotes);

public record SentimentRow(
    string Ticker, int BullishCalls, int BearishCalls, decimal NetSentiment,
    IReadOnlyList<string> BullishCreators, IReadOnlyList<string> BearishCreators, DateTime LatestCallAt);

public record HeadsUpDto(string Disclaimer, string? Brief, DateOnly? BriefDate, int WindowDays, IReadOnlyList<SentimentRow> Tickers);

public record InboxItemDto(ClaimDto Claim, bool WorthWatching, IReadOnlyList<string> Reasons);

public record InboxDayDto(DateOnly Date, IReadOnlyList<InboxItemDto> Items);

public record AskRequest([Required, MinLength(3), MaxLength(500)] string Question);

public record AskResponse(string Answer, IReadOnlyList<ClaimDto> Citations, string Disclaimer);

public record ClaimTradeDto(
    Guid OrderId, string Symbol, OrderSide Side, decimal Quantity, decimal? FillPrice, OrderStatus Status,
    decimal? CurrentPrice, decimal? ProfitLoss, Guid ClaimId, string ChannelTitle, ClaimDirection Direction, DateTime PlacedAt);
