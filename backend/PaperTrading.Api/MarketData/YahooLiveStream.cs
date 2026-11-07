using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PaperTrading.Api.MarketData;

/// <param name="MarketHours">The stream's session field: 1 is regular, 2 and 3 are after hours, and pre-market leaves it out.</param>
public record PriceTick(string Symbol, decimal Price, DateTime Time, int MarketHours);

/// <summary>
/// Reads the stream's messages, which are protocol buffers. Only the fields used here are read, by number:
/// 1 symbol, 2 price (32-bit float), 3 trade time (signed, in milliseconds), 7 session.
/// </summary>
public static class YahooPricing
{
    public static PriceTick? Decode(ReadOnlySpan<byte> data)
    {
        string? symbol = null;
        float price = 0;
        long timeMs = 0;
        var hours = 0;

        var i = 0;
        while (i < data.Length)
        {
            if (!TryReadVarint(data, ref i, out var tag)) return null;
            var field = (int)(tag >> 3);
            switch (tag & 7)
            {
                case 0:
                    if (!TryReadVarint(data, ref i, out var number)) return null;
                    if (field == 3) timeMs = (long)(number >> 1) ^ -(long)(number & 1);
                    else if (field == 7) hours = (int)number;
                    break;
                case 1:
                    i += 8;
                    break;
                case 2:
                    if (!TryReadVarint(data, ref i, out var length) || length > (ulong)(data.Length - i)) return null;
                    if (field == 1) symbol = Encoding.UTF8.GetString(data.Slice(i, (int)length));
                    i += (int)length;
                    break;
                case 5:
                    if (i + 4 > data.Length) return null;
                    if (field == 2) price = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i, 4));
                    i += 4;
                    break;
                default:
                    return null;
            }
        }

        if (symbol is null || !float.IsFinite(price) || timeMs <= 0) return null;
        DateTime time;
        try { time = DateTimeOffset.FromUnixTimeMilliseconds(timeMs).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
        return new PriceTick(symbol, ToPrice(price), time, hours);
    }

    /// <summary>
    /// A 32-bit float turns 344.63 into 344.6300048. Four places recovers the real price, including sub-penny trades.
    /// Above $1,000 a float can't hold that many, so two places avoids inventing digits.
    /// </summary>
    private static decimal ToPrice(float price) => Math.Round((decimal)(double)price, price >= 1000 ? 2 : 4);

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int i, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && i < data.Length; shift += 7)
        {
            var b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return true;
        }
        return false;
    }
}

/// <summary>
/// One connection to the stream Yahoo's own pages use, following whatever symbols people are watching.
/// It stays idle until something is being watched. If it drops, the regular batch check keeps prices moving.
/// </summary>
public sealed class YahooLiveStream(YahooMarketDataProvider provider, IMarketClock clock, ILogger<YahooLiveStream> log) : BackgroundService
{
    public const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64) PaperTrading/1.0";

    private static readonly Uri Address = new("wss://streamer.finance.yahoo.com/?version=2");
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>In market hours, this long with no message at all means the connection is stuck, even if it looks open.</summary>
    private static readonly TimeSpan MaxSilence = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (provider.LiveSymbols.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            var started = DateTime.UtcNow;
            try
            {
                await RunConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning("Live price stream dropped, reconnecting: {Message}", ex.Message);
            }
            finally
            {
                provider.IsStreaming = false;
            }

            if (DateTime.UtcNow - started > MaxBackoff) backoff = TimeSpan.FromSeconds(1);
            await Task.Delay(backoff, stoppingToken);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
        }
    }

    private async Task RunConnectionAsync(CancellationToken stoppingToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", UserAgent);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(Address, connectTimeout.Token);
        }
        provider.IsStreaming = true;
        log.LogInformation("Live price stream connected");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var lastMessage = DateTime.UtcNow;
        var receiving = ReceiveAsync(socket, () => lastMessage = DateTime.UtcNow, stop.Token);
        var following = FollowSymbolsAsync(socket, () => lastMessage, stop.Token);

        var finished = await Task.WhenAny(receiving, following);
        await stop.CancelAsync();
        await finished;
    }

    /// <summary>Keeps the stream's subscriptions matching what's being watched. Only this method sends.</summary>
    private async Task FollowSymbolsAsync(ClientWebSocket socket, Func<DateTime> lastMessage, CancellationToken ct)
    {
        var subscribed = new HashSet<string>();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            var wanted = provider.LiveSymbols;
            var add = wanted.Except(subscribed).ToList();
            var remove = subscribed.Except(wanted).ToList();
            if (add.Count > 0) await SendAsync(socket, new { subscribe = add }, ct);
            if (remove.Count > 0) await SendAsync(socket, new { unsubscribe = remove }, ct);
            subscribed.UnionWith(add);
            subscribed.ExceptWith(remove);

            if (subscribed.Count > 0 && clock.Status().IsOpen && DateTime.UtcNow - lastMessage() > MaxSilence)
                throw new TimeoutException($"No prices for {MaxSilence.TotalMinutes:0} minutes while the market is open.");
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task ReceiveAsync(ClientWebSocket socket, Action onMessage, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException($"Yahoo closed the connection ({result.CloseStatus}).");

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            onMessage();
            Handle(message.GetBuffer().AsSpan(0, (int)message.Length));
            message.SetLength(0);
        }
    }

    private void Handle(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            if (document.RootElement.TryGetProperty("message", out var message) && message.TryGetBytesFromBase64(out var bytes) &&
                YahooPricing.Decode(bytes) is { } tick)
            {
                provider.ApplyTick(tick);
            }
        }
        catch (JsonException ex)
        {
            log.LogDebug("Ignored an unreadable stream message: {Message}", ex.Message);
        }
    }

    private static Task SendAsync(ClientWebSocket socket, object message, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, ct);
}
