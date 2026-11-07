using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;
using PaperTrading.Api.Insights;
using PaperTrading.Api.MarketData;

namespace PaperTrading.Api.Controllers;

[Route("api/channels")]
public sealed class ChannelsController(AppDbContext db, ChannelService channels, ClaimQueries queries) : ApiController
{
    /// <summary>The channels this account follows.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<ChannelDto>> List(CancellationToken ct)
    {
        var followed = await db.ChannelFollows.AsNoTracking().Where(f => f.UserId == UserId).Select(f => f.ChannelId).ToListAsync(ct);
        var rows = await db.Channels.AsNoTracking().Where(c => followed.Contains(c.Id)).OrderBy(c => c.AddedAt).ToListAsync(ct);
        var stats = (await queries.LeaderboardAsync(ct)).ToDictionary(s => s.ChannelId);

        var result = new List<ChannelDto>(rows.Count);
        foreach (var channel in rows)
            result.Add(await queries.ToChannelDtoAsync(channel, UserId, stats.GetValueOrDefault(channel.Id), ct));
        return result;
    }

    [HttpPost, EnableRateLimiting("channels")]
    public async Task<ActionResult<ChannelDto>> Add(AddChannelRequest request, CancellationToken ct)
    {
        var channel = await channels.AddAsync(UserId, request.Link, request.Backfill, ct);
        return Created($"/api/channels/{channel.Id}", await queries.ToChannelDtoAsync(channel, UserId, null, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ChannelDetailDto>> Detail(Guid id, CancellationToken ct)
    {
        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (channel is null) return NotFound(new ProblemDetails { Title = "That channel isn't here." });

        var stats = (await queries.LeaderboardAsync(ct)).FirstOrDefault(s => s.ChannelId == id);
        var videos = await db.Videos.AsNoTracking().Where(v => v.ChannelId == id).ToListAsync(ct);
        var rawClaims = await db.Claims.AsNoTracking().Where(c => c.ChannelId == id).ToListAsync(ct);
        var claimCounts = rawClaims.GroupBy(c => c.VideoId).ToDictionary(g => g.Key, g => g.Count());

        return new ChannelDetailDto(
            await queries.ToChannelDtoAsync(channel, UserId, stats, ct),
            videos
                .OrderByDescending(v => v.PublishedAt ?? DateTime.MaxValue).ThenBy(v => v.DiscoveredAt)
                .Select(v => new VideoDto(v.Id, v.YouTubeVideoId, v.Title, v.PublishedAt, v.Status, v.LastError, claimCounts.GetValueOrDefault(v.Id), v.RejectedClaims, v.NextAttemptAt))
                .ToList(),
            await queries.ClaimsAsync(db.Claims.Where(c => c.ChannelId == id), ct),
            ClaimQueries.FindReversals(rawClaims));
    }

    [HttpDelete("{id:guid}/follow")]
    public async Task<IActionResult> Unfollow(Guid id, CancellationToken ct)
    {
        await db.ChannelFollows.Where(f => f.UserId == UserId && f.ChannelId == id).ExecuteDeleteAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/follow")]
    public async Task<IActionResult> Follow(Guid id, CancellationToken ct)
    {
        if (!await db.Channels.AnyAsync(c => c.Id == id, ct)) return NotFound(new ProblemDetails { Title = "That channel isn't here." });
        if (!await db.ChannelFollows.AnyAsync(f => f.UserId == UserId && f.ChannelId == id, ct))
        {
            db.ChannelFollows.Add(new ChannelFollow { UserId = UserId, ChannelId = id });
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<object> Retry(Guid id, CancellationToken ct) => new { retried = await channels.RetryAsync(id, ct) };
}

[Route("api/claims")]
public sealed class ClaimsController(AppDbContext db, ClaimQueries queries) : ApiController
{
    [HttpGet]
    public async Task<IReadOnlyList<ClaimDto>> List([FromQuery] string? ticker, CancellationToken ct)
    {
        var query = db.Claims.AsQueryable();
        if (!string.IsNullOrWhiteSpace(ticker))
        {
            var symbol = Symbols.Normalize(ticker);
            query = query.Where(c => c.Ticker == symbol);
        }
        return await queries.ClaimsAsync(query.OrderByDescending(c => c.MadeAt).Take(200), ct);
    }

    /// <summary>Records a person's check of an extracted claim. An incorrect one stops counting towards rankings.</summary>
    [HttpPost("{id:guid}/review")]
    public async Task<IActionResult> Review(Guid id, ReviewRequest request, CancellationToken ct)
    {
        var claim = await db.Claims.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (claim is null) return NotFound(new ProblemDetails { Title = "That prediction isn't here." });

        claim.Review = request.Review;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

[Route("api/insights")]
public sealed class InsightsController(
    AppDbContext db, ClaimQueries queries, DigestService digest, ILlmClient llm, PipelineState state, IOptions<InsightsOptions> options) : ApiController
{
    [HttpGet("leaderboard")]
    public Task<IReadOnlyList<CreatorStats>> Leaderboard(CancellationToken ct) => queries.LeaderboardAsync(ct);

    [HttpGet("heads-up")]
    public Task<HeadsUpDto> HeadsUp([FromQuery] bool followedOnly, CancellationToken ct) => digest.HeadsUpAsync(UserId, followedOnly, ct);

    [HttpGet("inbox")]
    public Task<IReadOnlyList<InboxDayDto>> Inbox([FromQuery] int days = 14, CancellationToken ct = default) => digest.InboxAsync(UserId, days, ct);

    [HttpPost("ask"), EnableRateLimiting("ask")]
    public Task<AskResponse> Ask(AskRequest request, CancellationToken ct) => digest.AskAsync(request.Question, ct);

    [HttpGet("status")]
    public async Task<PipelineStatusDto> Status(CancellationToken ct)
    {
        var tokens = await db.Videos.AsNoTracking().Select(v => new { v.InputTokens, v.OutputTokens }).ToListAsync(ct);
        return new PipelineStatusDto(
            llm.IsConfigured, llm.Model,
            state.LlmPausedUntil > DateTime.UtcNow ? state.LlmPauseReason : null, state.LlmPausedUntil,
            state.YouTubePausedUntil > DateTime.UtcNow ? state.YouTubePauseReason : null, state.YouTubePausedUntil,
            !string.IsNullOrWhiteSpace(options.Value.WhisperCommand),
            options.Value.MinCallsToRank, options.Value.DefaultHorizonDays, options.Value.Benchmark,
            tokens.Sum(t => t.InputTokens), tokens.Sum(t => t.OutputTokens), state.Running);
    }

    /// <summary>Starts fetching and processing videos.</summary>
    [HttpPost("pipeline/start")]
    public IActionResult StartPipeline()
    {
        state.Start();
        return NoContent();
    }

    /// <summary>Stops fetching straight away, cancelling any request in flight. Nothing queued is lost.</summary>
    [HttpPost("pipeline/stop")]
    public IActionResult StopPipeline()
    {
        state.Stop();
        return NoContent();
    }

    /// <summary>How often the extraction was right, measured from claims people have checked.</summary>
    [HttpGet("accuracy")]
    public async Task<AccuracyDto> Accuracy(CancellationToken ct)
    {
        var claims = await db.Claims.AsNoTracking().Select(c => new { c.Review, c.QuoteVerified }).ToListAsync(ct);
        var correct = claims.Count(c => c.Review == ClaimReview.Correct);
        var incorrect = claims.Count(c => c.Review == ClaimReview.Incorrect);
        var reviewed = correct + incorrect;
        return new AccuracyDto(
            reviewed, correct, incorrect,
            reviewed == 0 ? null : Math.Round(correct / (decimal)reviewed * 100, 1),
            claims.Count, claims.Count(c => !c.QuoteVerified));
    }

    /// <summary>Paper trades placed from a creator's call, and how they've done.</summary>
    [HttpGet("claim-trades")]
    public async Task<IReadOnlyList<ClaimTradeDto>> ClaimTrades([FromServices] IMarketDataProvider market, CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.UserId == UserId && o.ClaimId != null)
            .OrderByDescending(o => o.CreatedAt)
            .Take(200)
            .ToListAsync(ct);
        if (orders.Count == 0) return [];

        var claimIds = orders.Select(o => o.ClaimId!.Value).Distinct().ToList();
        var claims = await db.Claims.AsNoTracking()
            .Where(c => claimIds.Contains(c.Id))
            .Join(db.Channels.AsNoTracking(), c => c.ChannelId, ch => ch.Id, (c, ch) => new { c.Id, c.Direction, ch.Title })
            .ToDictionaryAsync(x => x.Id, ct);
        var fills = await db.Fills.AsNoTracking().Where(f => f.UserId == UserId).ToListAsync(ct);
        var quotes = await market.GetQuotesAsync(orders.Select(o => o.Symbol), ct);

        return orders.Where(o => claims.ContainsKey(o.ClaimId!.Value)).Select(o =>
        {
            var claim = claims[o.ClaimId!.Value];
            var fill = fills.FirstOrDefault(f => f.OrderId == o.Id);
            var current = quotes.GetValueOrDefault(o.Symbol)?.Price;
            decimal? profit = fill is null ? null
                : fill.RealizedPnl ?? (current is { } price ? fill.Quantity * (price - fill.Price) - fill.Commission : null);
            return new ClaimTradeDto(o.Id, o.Symbol, o.Side, o.Quantity, o.FilledPrice, o.Status, current, profit, claim.Id, claim.Title, claim.Direction, o.CreatedAt);
        }).ToList();
    }
}
