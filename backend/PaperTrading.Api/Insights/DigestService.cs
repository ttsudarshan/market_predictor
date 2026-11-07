using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;

namespace PaperTrading.Api.Insights;

/// <summary>The market heads-up, the daily inbox and plain-English questions over stored claims.</summary>
public sealed class DigestService(
    AppDbContext db, ClaimQueries queries, ILlmClient llm, IOptions<InsightsOptions> options)
{
    public const string Disclaimer = "This shows what creators are saying, weighted by their track record. It's sentiment, not a forecast, and not financial advice.";

    private const int MaxClaimsForQuestion = 300;
    private readonly InsightsOptions _options = options.Value;

    /// <summary>What creators are calling right now, per ticker, counting better track records for more.</summary>
    public async Task<IReadOnlyList<SentimentRow>> SentimentAsync(Guid? followedBy, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-_options.HeadsUpDays);
        var claims = await db.Claims.AsNoTracking()
            .Where(c => c.MadeAt >= since && c.QuoteVerified && c.Status == ClaimStatus.Pending && c.Review != ClaimReview.Incorrect)
            .ToListAsync(ct);

        if (followedBy is { } userId)
        {
            var followed = await db.ChannelFollows.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.ChannelId).ToListAsync(ct);
            claims = claims.Where(c => followed.Contains(c.ChannelId)).ToList();
        }

        var stats = (await queries.LeaderboardAsync(ct)).ToDictionary(s => s.ChannelId);
        return BuildSentiment(claims, stats);
    }

    public static IReadOnlyList<SentimentRow> BuildSentiment(IEnumerable<Claim> claims, IReadOnlyDictionary<Guid, CreatorStats> stats)
    {
        return claims.GroupBy(c => c.Ticker).Select(group =>
        {
            // Each creator's latest view counts once, however many videos repeat it.
            var latest = group.GroupBy(c => c.ChannelId).Select(g => g.OrderByDescending(c => c.MadeAt).First()).ToList();
            var bullish = latest.Where(c => c.Direction == ClaimDirection.Bullish).ToList();
            var bearish = latest.Where(c => c.Direction == ClaimDirection.Bearish).ToList();

            decimal Weight(IEnumerable<Claim> items) => items.Sum(c => stats.TryGetValue(c.ChannelId, out var s) ? s.TrustWeight : 1m);
            var bullWeight = Weight(bullish);
            var bearWeight = Weight(bearish);
            var total = bullWeight + bearWeight;

            string Name(Claim c) => stats.TryGetValue(c.ChannelId, out var s) ? s.Title : "Unknown";
            return new SentimentRow(
                group.Key, bullish.Count, bearish.Count,
                total == 0 ? 0 : Math.Round((bullWeight - bearWeight) / total, 3),
                bullish.Select(Name).ToList(), bearish.Select(Name).ToList(),
                latest.Max(c => c.MadeAt));
        })
        .OrderByDescending(r => r.BullishCalls + r.BearishCalls)
        .ThenByDescending(r => Math.Abs(r.NetSentiment))
        .ToList();
    }

    public async Task<HeadsUpDto> HeadsUpAsync(Guid userId, bool followedOnly, CancellationToken ct = default)
    {
        var rows = await SentimentAsync(followedOnly ? userId : null, ct);
        var brief = await db.DailyBriefs.AsNoTracking().OrderByDescending(b => b.Date).FirstOrDefaultAsync(ct);
        return new HeadsUpDto(Disclaimer, brief?.Summary, brief?.Date, _options.HeadsUpDays, rows);
    }

    /// <summary>Writes today's summary once, and only when there's something to summarize.</summary>
    public async Task WriteDailyBriefAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (await db.DailyBriefs.AnyAsync(b => b.Date == today, ct)) return;

        var rows = await SentimentAsync(null, ct);
        if (rows.Count == 0) return;

        const string system = """
            You write a short daily heads-up for one person who follows finance YouTubers. You receive the current calls per ticker, each weighted by how accurate that creator has been, and net_sentiment runs from -1 (all bearish) to 1 (all bullish).

            In 3 to 5 plain sentences, say where creators agree, where they disagree, and what changed most. Name tickers and creators. Report what creators are saying, not what the market will do: don't forecast prices, recommend trades, or add facts that aren't in the data.
            """;
        var data = JsonSerializer.Serialize(rows.Take(25).Select(r => new
        {
            ticker = r.Ticker,
            net_sentiment = r.NetSentiment,
            bullish = r.BullishCreators,
            bearish = r.BearishCreators,
        }));

        var result = await llm.CompleteJsonAsync(system, $"<calls>{data}</calls>", BriefSchema, ct);
        var summary = JsonSerializer.Deserialize<BriefResponse>(result.Text)?.Summary;
        if (string.IsNullOrWhiteSpace(summary)) return;

        db.DailyBriefs.Add(new DailyBrief { Date = today, Summary = summary.Trim() });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Recent calls from followed creators, by day, each with any reasons it deserves attention.</summary>
    public async Task<IReadOnlyList<InboxDayDto>> InboxAsync(Guid userId, int days, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 90));
        var followed = await db.ChannelFollows.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.ChannelId).ToListAsync(ct);
        var claims = await queries.ClaimsAsync(db.Claims.Where(c => c.MadeAt >= since && followed.Contains(c.ChannelId)), ct);

        // A top rank is only worth pointing out if that creator actually beats the market.
        var stats = await queries.LeaderboardAsync(ct);
        var ranks = stats.Where(s => s.Rank is not null && s.AverageExcessReturn > 0).ToDictionary(s => s.ChannelId, s => s.Rank!.Value);
        var held = await db.Positions.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.Symbol).ToListAsync(ct);
        var lists = await db.WatchLists.AsNoTracking().Where(l => l.UserId == userId).Select(l => l.Id).ToListAsync(ct);
        var watched = await db.WatchlistItems.AsNoTracking().Where(i => lists.Contains(i.WatchListId)).Select(i => i.Symbol).ToListAsync(ct);

        var creatorsByTicker = claims
            .Where(Counts)
            .GroupBy(c => (c.Ticker, c.Direction))
            .ToDictionary(g => g.Key, g => (IReadOnlySet<Guid>)g.Select(c => c.ChannelId).ToHashSet());

        return claims
            .GroupBy(c => DateOnly.FromDateTime(c.MadeAt))
            .OrderByDescending(g => g.Key)
            .Select(g => new InboxDayDto(g.Key, g.Select(c =>
            {
                var reasons = WorthWatchingReasons(c, ranks, held, watched, creatorsByTicker);
                return new InboxItemDto(c, reasons.Count >= 2, reasons);
            }).OrderByDescending(i => i.Reasons.Count).ToList()))
            .ToList();
    }

    private static bool Counts(ClaimDto c) => c.QuoteVerified && c.Review != ClaimReview.Incorrect;

    public static List<string> WorthWatchingReasons(
        ClaimDto c, IReadOnlyDictionary<Guid, int> ranks, IReadOnlyCollection<string> held, IReadOnlyCollection<string> watched,
        IReadOnlyDictionary<(string, ClaimDirection), IReadOnlySet<Guid>> creatorsByTicker)
    {
        var reasons = new List<string>();
        if (!Counts(c)) return reasons;

        if (ranks.TryGetValue(c.ChannelId, out var rank) && rank <= 3) reasons.Add($"From the #{rank} ranked creator");
        if (c.TargetPrice is not null && c.HorizonStated) reasons.Add("Specific price and timeframe");
        if (held.Contains(c.Ticker)) reasons.Add("You hold this");
        else if (watched.Contains(c.Ticker)) reasons.Add("On your lists");
        if (creatorsByTicker.TryGetValue((c.Ticker, c.Direction), out var agreeing) && agreeing.Count >= 2)
            reasons.Add($"{agreeing.Count} creators agree");

        // One creator changing their mind isn't a disagreement between creators.
        var opposite = c.Direction == ClaimDirection.Bullish ? ClaimDirection.Bearish : ClaimDirection.Bullish;
        if (creatorsByTicker.TryGetValue((c.Ticker, opposite), out var opposing) && opposing.Any(id => id != c.ChannelId))
            reasons.Add("Creators disagree on this");
        return reasons;
    }

    /// <summary>
    /// Answers a question from stored claims. Claims are short, so at this scale the relevant ones fit directly in
    /// the prompt, and no vector search is needed. Tickers and creators named in the question narrow the selection.
    /// </summary>
    public async Task<AskResponse> AskAsync(string question, CancellationToken ct = default)
    {
        if (!llm.IsConfigured) throw new TradingException("Add an API key to ask questions.", StatusCodes.Status503ServiceUnavailable);

        var all = await queries.ClaimsAsync(db.Claims.Where(c => c.QuoteVerified && c.Review != ClaimReview.Incorrect), ct);
        var selected = SelectForQuestion(all, question);
        var stats = await queries.LeaderboardAsync(ct);

        const string system = """
            You answer questions about what finance YouTubers have predicted, using only the claims and track records provided. Each claim has an id, and score fields once its outcome is known. A claim with status Pending hasn't reached its date yet, so its return is provisional.

            Answer plainly in a few sentences, and put the ids of the claims you relied on in cited_claims. If the data doesn't answer the question, say so instead of guessing. Report what was said and how it turned out, and don't give investment advice or forecast prices yourself.
            """;

        var payload = JsonSerializer.Serialize(new
        {
            today = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            track_records = stats.Where(s => s.TotalCalls > 0).Select(s => new
            {
                creator = s.Title, rank = s.Rank, finished_calls = s.FinalCalls, pending_calls = s.PendingCalls,
                hit_rate = s.HitRate, average_excess_return_vs_benchmark = s.AverageExcessReturn,
            }),
            claims = selected.Select((c, i) => new
            {
                id = $"c{i}", creator = c.ChannelTitle, ticker = c.Ticker, direction = c.Direction.ToString(),
                date = c.MadeAt.ToString("yyyy-MM-dd"), resolves = c.ResolvesAt.ToString("yyyy-MM-dd"),
                target = c.TargetPrice, status = c.Status.ToString(), return_percent = c.ReturnPercent,
                excess_return_percent = c.ExcessReturnPercent, target_hit = c.TargetHit, quote = c.Quote, reasoning = c.Reasoning,
            }),
        });

        var result = await llm.CompleteJsonAsync(system, $"<data>{payload}</data>\n\n<question>{question}</question>", AskSchema, ct);
        var parsed = JsonSerializer.Deserialize<AskModelResponse>(result.Text);
        if (string.IsNullOrWhiteSpace(parsed?.Answer)) throw new LlmRejectedException("The model returned no answer.");

        // Citations come from what was actually sent, so a made-up id can't point at anything.
        var citations = (parsed.CitedClaims ?? [])
            .Select(id => int.TryParse(id.TrimStart('c'), out var i) && i >= 0 && i < selected.Count ? selected[i] : null)
            .OfType<ClaimDto>()
            .DistinctBy(c => c.Id)
            .ToList();
        return new AskResponse(parsed.Answer.Trim(), citations, Disclaimer);
    }

    public static List<ClaimDto> SelectForQuestion(IReadOnlyList<ClaimDto> claims, string question)
    {
        var words = question.ToUpperInvariant()
            .Split(new[] { ' ', ',', '.', '?', '!', '\'', '"', '$', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();
        var lower = question.ToLowerInvariant();

        var matched = claims.Where(c => words.Contains(c.Ticker) || lower.Contains(c.ChannelTitle.ToLowerInvariant())).ToList();
        return (matched.Count > 0 ? matched : claims).Take(MaxClaimsForQuestion).ToList();
    }

    private static readonly object BriefSchema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "summary" },
        properties = new { summary = new { type = "string" } },
    };

    private static readonly object AskSchema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "answer", "cited_claims" },
        properties = new
        {
            answer = new { type = "string" },
            cited_claims = new { type = "array", items = new { type = "string" } },
        },
    };

    private sealed record BriefResponse([property: JsonPropertyName("summary")] string? Summary);

    private sealed record AskModelResponse(
        [property: JsonPropertyName("answer")] string? Answer,
        [property: JsonPropertyName("cited_claims")] string[]? CitedClaims);
}
