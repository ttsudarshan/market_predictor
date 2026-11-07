using System.Net;
using System.Text.Json;
using PaperTrading.Api.Domain;

namespace PaperTrading.Api.MarketData;

public record DailyBar(DateOnly Date, decimal High, decimal Low, decimal Close, DateTime ClosesAt);

/// <summary>
/// Real daily prices for scoring predictions. This never goes through <see cref="IMarketDataProvider"/>:
/// on the simulated feed that would be a random walk, and every score would be invented.
/// </summary>
public interface IPriceHistory
{
    /// <returns>Bars oldest first, or an empty list when the symbol doesn't exist.</returns>
    /// <exception cref="MarketDataUnavailableException">Yahoo couldn't answer.</exception>
    Task<IReadOnlyList<DailyBar>> GetDailyAsync(string symbol, DateOnly from, CancellationToken ct = default);
}

public sealed class YahooPriceHistory(IHttpClientFactory httpFactory) : IPriceHistory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _concurrency = new(2);

    public async Task<IReadOnlyList<DailyBar>> GetDailyAsync(string symbol, DateOnly from, CancellationToken ct = default)
    {
        var period1 = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var period2 = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var url = $"v8/finance/chart/{Uri.EscapeDataString(symbol)}?period1={period1}&period2={period2}&interval=1d";

        await _concurrency.WaitAsync(ct);
        try
        {
            var http = httpFactory.CreateClient(YahooMarketDataProvider.HttpClientName);
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return [];
            if (!response.IsSuccessStatusCode)
                throw new MarketDataUnavailableException($"Yahoo Finance returned HTTP {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var body = await JsonSerializer.DeserializeAsync<Envelope>(stream, Json, ct);
            var result = body?.Chart?.Result?.FirstOrDefault();
            return result is null ? [] : ToBars(result);
        }
        catch (HttpRequestException ex)
        {
            throw new MarketDataUnavailableException("Yahoo Finance could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MarketDataUnavailableException("Yahoo Finance timed out.", ex);
        }
        catch (JsonException ex)
        {
            throw new MarketDataUnavailableException("Yahoo Finance returned an unexpected response.", ex);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private static List<DailyBar> ToBars(Result result)
    {
        var q = result.Indicators?.Quote?.FirstOrDefault();
        if (q is null || result.Timestamp is null) return [];

        // Bars carry their opening time, so work out when that exchange's trading day ended. This has to be done
        // bar by bar: the UTC time of the close moves with daylight saving, and some days close early.
        var zoneName = result.Meta?.ExchangeTimezoneName ?? NyseCalendar.TimeZoneId;
        var isNewYork = zoneName == NyseCalendar.TimeZoneId || !TimeZoneInfo.TryFindSystemTimeZoneById(zoneName, out _);
        var zone = isNewYork ? NyseCalendar.Zone : TimeZoneInfo.FindSystemTimeZoneById(zoneName);
        var localClose = result.Meta?.CurrentTradingPeriod?.Regular?.End is { } end
            ? TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime, zone))
            : new TimeOnly(16, 0);

        var bars = new List<DailyBar>(result.Timestamp.Length);
        for (var i = 0; i < result.Timestamp.Length; i++)
        {
            if (q.High?.ElementAtOrDefault(i) is not { } high || q.Low?.ElementAtOrDefault(i) is not { } low ||
                q.Close?.ElementAtOrDefault(i) is not { } close) continue;

            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(result.Timestamp[i]).UtcDateTime, zone));
            var closesAt = isNewYork
                ? NyseCalendar.CloseUtc(date)
                : TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(localClose, DateTimeKind.Unspecified), zone);
            bars.Add(new DailyBar(date, Math.Round(high, 4), Math.Round(low, 4), Math.Round(close, 4), closesAt));
        }
        return bars;
    }

    private sealed record Envelope(Body? Chart);
    private sealed record Body(Result[]? Result);
    private sealed record Result(Meta? Meta, long[]? Timestamp, Indicators? Indicators);
    private sealed record Meta(string? ExchangeTimezoneName, Periods? CurrentTradingPeriod);
    private sealed record Periods(Period? Regular);
    private sealed record Period(long End);
    private sealed record Indicators(Series[]? Quote);
    private sealed record Series(decimal?[]? High, decimal?[]? Low, decimal?[]? Close);
}
