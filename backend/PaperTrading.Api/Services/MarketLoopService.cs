using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperTrading.Api.Contracts;
using PaperTrading.Api.Data;
using PaperTrading.Api.Domain;
using PaperTrading.Api.Hubs;
using PaperTrading.Api.MarketData;

namespace PaperTrading.Api.Services;

/// <summary>Refreshes prices on a timer, passes on streamed ones as they arrive, fills resting orders and pushes updates.</summary>
public sealed class MarketLoopService(
    IServiceScopeFactory scopes,
    IMarketDataProvider market,
    SubscriptionRegistry subscriptions,
    IHubContext<MarketHub> hub,
    IOptions<TradingOptions> options,
    ILogger<MarketLoopService> log) : BackgroundService
{
    /// <summary>Checks for streamed prices this often, so a screen and resting orders see them within a second.</summary>
    private static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(1);

    private DateTime _nextSnapshot = DateTime.UtcNow.AddMinutes(1);
    private DateTime _nextPoll = DateTime.MinValue;
    private IReadOnlyList<string> _orderSymbols = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(market.PollInterval < LiveInterval ? market.PollInterval : LiveInterval);
        do
        {
            try
            {
                if (DateTime.UtcNow >= _nextPoll)
                {
                    _nextPoll = DateTime.UtcNow + market.PollInterval;
                    await PollAsync(stoppingToken);
                }
                market.SetLiveSymbols(subscriptions.AllSymbols().Union(_orderSymbols));
                await PublishLiveUpdatesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad tick must not kill the loop, or resting orders would silently stop filling.
                log.LogError(ex, "Market tick failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PublishLiveUpdatesAsync(CancellationToken ct)
    {
        var updates = market.TakeLiveUpdates();
        if (updates.Count == 0) return;

        await using var scope = scopes.CreateAsyncScope();
        await MatchAndPublishAsync(scope.ServiceProvider.GetRequiredService<OrderService>(), updates.ToDictionary(q => q.Symbol), ct);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _orderSymbols = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Open)
            .Select(o => o.Symbol).Distinct().ToListAsync(ct);

        var symbols = subscriptions.AllSymbols().Union(_orderSymbols).ToList();
        if (symbols.Count > 0)
        {
            var quotes = await market.GetQuotesAsync(symbols, ct);
            await MatchAndPublishAsync(scope.ServiceProvider.GetRequiredService<OrderService>(), quotes, ct);
        }

        if (DateTime.UtcNow >= _nextSnapshot)
        {
            _nextSnapshot = DateTime.UtcNow.AddMinutes(options.Value.SnapshotMinutes);
            await RecordSnapshotsAsync(scope.ServiceProvider, db, ct);
        }
    }

    private async Task MatchAndPublishAsync(OrderService orders, IReadOnlyDictionary<string, Quote> quotes, CancellationToken ct)
    {
        foreach (var order in await orders.MatchOpenOrdersAsync(quotes, ct))
            await hub.Clients.User(order.UserId.ToString()).SendAsync("orderUpdated", OrderDto.From(order), ct);

        foreach (var quote in quotes.Values)
            await hub.Clients.Group(MarketHub.GroupFor(quote.Symbol)).SendAsync("quote", quote, ct);
    }

    private async Task RecordSnapshotsAsync(IServiceProvider services, AppDbContext db, CancellationToken ct)
    {
        var portfolio = services.GetRequiredService<PortfolioService>();
        var userIds = await db.Users.AsNoTracking().Select(u => u.Id).ToListAsync(ct);
        foreach (var id in userIds)
        {
            try { await portfolio.RecordSnapshotAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Snapshot failed for user {UserId}", id);
            }
        }
    }
}
