using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PaperTrading.Api.Domain;
using PaperTrading.Api.MarketData;

namespace PaperTrading.Api.Hubs;

[Authorize]
public sealed class MarketHub(SubscriptionRegistry registry, IMarketDataProvider market) : Hub
{
    private const int MaxSymbolsPerConnection = 100;

    public static string GroupFor(string symbol) => $"quote:{symbol}";

    public async Task Subscribe(string[] symbols)
    {
        var valid = symbols
            .Select(s => Symbols.TryNormalize(s, out var n) ? n : null)
            .OfType<string>().Distinct().ToList();
        var added = registry.Add(Context.ConnectionId, valid, MaxSymbolsPerConnection);

        foreach (var symbol in added)
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(symbol));

        // Send current prices right away instead of leaving the screen blank until the next tick.
        var quotes = await market.GetQuotesAsync(added, Context.ConnectionAborted);
        foreach (var quote in quotes.Values)
            await Clients.Caller.SendAsync("quote", quote, Context.ConnectionAborted);
    }

    public async Task Unsubscribe(string[] symbols)
    {
        foreach (var symbol in registry.Remove(Context.ConnectionId, symbols))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(symbol));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Tracks which symbols connected clients are watching, so only those get polled.</summary>
public sealed class SubscriptionRegistry
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _byConnection = new();

    public IReadOnlyList<string> Add(string connectionId, IEnumerable<string> symbols, int maxPerConnection)
    {
        var set = _byConnection.GetOrAdd(connectionId, _ => []);
        var added = new List<string>();
        lock (set)
        {
            foreach (var symbol in symbols)
            {
                if (set.Count >= maxPerConnection) break;
                if (set.Add(symbol)) added.Add(symbol);
            }
        }
        return added;
    }

    public IReadOnlyList<string> Remove(string connectionId, IEnumerable<string> symbols)
    {
        if (!_byConnection.TryGetValue(connectionId, out var set)) return [];
        lock (set)
        {
            return symbols.Select(s => s.ToUpperInvariant()).Where(set.Remove).ToList();
        }
    }

    public void RemoveConnection(string connectionId) => _byConnection.TryRemove(connectionId, out _);

    public IReadOnlyList<string> AllSymbols()
    {
        var all = new HashSet<string>();
        foreach (var set in _byConnection.Values)
        {
            lock (set) all.UnionWith(set);
        }
        return all.ToList();
    }
}
