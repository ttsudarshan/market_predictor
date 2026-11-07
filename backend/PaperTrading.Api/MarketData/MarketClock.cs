using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;

namespace PaperTrading.Api.MarketData;

public enum MarketPhase { Open, PreMarket, AfterHours, Overnight, Weekend, Holiday }

/// <param name="Label">Short state, such as "After hours".</param>
/// <param name="Detail">What happens next, with times in Eastern Time.</param>
/// <param name="SessionStart">When the current or most recent session opened. Never in the future.</param>
public record MarketStatus(
    MarketPhase Phase, string Label, string Detail, DateTime? NextOpen, DateTime? ClosesAt, DateTime? SessionStart)
{
    public bool IsOpen => Phase == MarketPhase.Open;
}

/// <summary>
/// A session that Yahoo reports for an exchange outside New York, such as NSE when the account trades in INR.
/// Null on a quote means the US market, which follows <see cref="NyseCalendar"/>.
/// </summary>
public record ExchangeSession(string TimeZoneName, DateTime Start, DateTime End);

/// <summary>
/// The NYSE trading calendar, worked out from the exchange's rules so it also covers the past dates scoring needs.
/// The tests check it day by day against https://www.nyse.com/trade/hours-calendars (2026 to 2028, read 2026-11-02).
/// Unscheduled closures can't be known in advance. <see cref="MarketClock"/> catches those by refusing old prices.
/// </summary>
public static class NyseCalendar
{
    public const string TimeZoneId = "America/New_York";
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    private static readonly TimeOnly PreMarketStart = new(4, 0);
    private static readonly TimeOnly OpenTime = new(9, 30);
    private static readonly TimeOnly FullClose = new(16, 0);
    private static readonly TimeOnly EarlyClose = new(13, 0);
    private static readonly TimeSpan AfterHoursLength = TimeSpan.FromHours(4);

    private static readonly ConcurrentDictionary<int, Dictionary<DateOnly, string>> HolidaysByYear = new();

    public static DateOnly DateInNewYork(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, Zone));

    public static string? HolidayName(DateOnly date) =>
        HolidaysByYear.GetOrAdd(date.Year, BuildHolidays).GetValueOrDefault(date);

    public static bool IsTradingDay(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && HolidayName(date) is null;

    public static bool IsEarlyClose(DateOnly date) =>
        IsTradingDay(date) && (
            date is { Month: 7, Day: 3 } or { Month: 12, Day: 24 } ||
            date == NthWeekday(date.Year, 11, DayOfWeek.Thursday, 4).AddDays(1));

    public static DateTime OpenUtc(DateOnly date) => ToUtc(date, OpenTime);
    public static DateTime CloseUtc(DateOnly date) => ToUtc(date, IsEarlyClose(date) ? EarlyClose : FullClose);

    public static MarketStatus StatusAt(DateTime utc)
    {
        var today = DateInNewYork(utc);
        var nextOpen = NextOpenAfter(utc);
        var sessionStart = LatestOpenAtOrBefore(utc);
        var opens = OpensText(today, nextOpen);

        if (!IsTradingDay(today))
        {
            return HolidayName(today) is { } holiday
                ? new(MarketPhase.Holiday, $"Closed for {holiday}", opens, nextOpen, null, sessionStart)
                : new(MarketPhase.Weekend, "Closed for the weekend", opens, nextOpen, null, sessionStart);
        }

        var open = OpenUtc(today);
        var close = CloseUtc(today);
        if (utc >= open && utc < close)
            return new(MarketPhase.Open, "Market open", $"Closes at {Time(close)} ET", nextOpen, close, sessionStart);
        if (utc >= close && utc < close + AfterHoursLength)
            return new(MarketPhase.AfterHours, "After hours", opens, nextOpen, null, sessionStart);
        if (utc < open && utc >= ToUtc(today, PreMarketStart))
            return new(MarketPhase.PreMarket, "Pre-market", opens, nextOpen, null, sessionStart);
        return new(MarketPhase.Overnight, "Closed overnight", opens, nextOpen, null, sessionStart);
    }

    private static DateTime NextOpenAfter(DateTime utc)
    {
        var date = DateInNewYork(utc);
        while (!IsTradingDay(date) || OpenUtc(date) <= utc) date = date.AddDays(1);
        return OpenUtc(date);
    }

    private static DateTime LatestOpenAtOrBefore(DateTime utc)
    {
        var date = DateInNewYork(utc);
        while (!IsTradingDay(date) || OpenUtc(date) > utc) date = date.AddDays(-1);
        return OpenUtc(date);
    }

    private static string OpensText(DateOnly today, DateTime nextOpen)
    {
        var day = DateInNewYork(nextOpen);
        return day == today
            ? $"Opens at {Time(nextOpen)} ET"
            : $"Opens {day.ToString("ddd, MMM d", CultureInfo.InvariantCulture)} at {Time(nextOpen)} ET";
    }

    private static string Time(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, Zone).ToString("h:mm tt", CultureInfo.InvariantCulture);

    private static DateTime ToUtc(DateOnly date, TimeOnly time) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), Zone);

    private static Dictionary<DateOnly, string> BuildHolidays(int year)
    {
        var goodFriday = EasterSunday(year).AddDays(-2);
        var holidays = new Dictionary<DateOnly, string>
        {
            [NthWeekday(year, 1, DayOfWeek.Monday, 3)] = "Martin Luther King Jr. Day",
            [NthWeekday(year, 2, DayOfWeek.Monday, 3)] = "Washington's Birthday",
            [goodFriday] = "Good Friday",
            [LastWeekday(year, 5, DayOfWeek.Monday)] = "Memorial Day",
            [Observed(new DateOnly(year, 7, 4))] = "Independence Day",
            [NthWeekday(year, 9, DayOfWeek.Monday, 1)] = "Labor Day",
            [NthWeekday(year, 11, DayOfWeek.Thursday, 4)] = "Thanksgiving Day",
            [Observed(new DateOnly(year, 12, 25))] = "Christmas Day",
        };

        // A Saturday New Year's Day isn't moved to December 31, as that would close the market on the last day of the year.
        var newYear = new DateOnly(year, 1, 1);
        if (newYear.DayOfWeek != DayOfWeek.Saturday) holidays[Observed(newYear)] = "New Year's Day";

        if (year >= 2022) holidays[Observed(new DateOnly(year, 6, 19))] = "Juneteenth";
        return holidays;
    }

    private static DateOnly Observed(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Saturday => date.AddDays(-1),
        DayOfWeek.Sunday => date.AddDays(1),
        _ => date,
    };

    private static DateOnly NthWeekday(int year, int month, DayOfWeek weekday, int n)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + (n - 1) * 7);
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek weekday)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return last.AddDays(-(((int)last.DayOfWeek - (int)weekday + 7) % 7));
    }

    /// <summary>The Gregorian Easter calculation (Meeus/Jones/Butcher).</summary>
    private static DateOnly EasterSunday(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        return new DateOnly(year, (h + l - 7 * m + 114) / 31, (h + l - 7 * m + 114) % 31 + 1);
    }
}

/// <summary>Decides whether trades can happen. Every order goes through this, so there's one answer to that question.</summary>
public interface IMarketClock
{
    /// <summary>The US market right now.</summary>
    MarketStatus Status();

    MarketStatus StatusFor(ExchangeSession? session);

    /// <returns>Why an order can't fill at this quote right now, or null if it can.</returns>
    string? WhyCannotFill(Quote quote);
}

/// <summary>The real market: trades fill only in the regular session, and only on a price from that session.</summary>
public sealed class MarketClock(TimeProvider time) : IMarketClock
{
    /// <summary>A last trade this old suggests a halted stock or a stuck feed. Long enough to allow for a delayed feed.</summary>
    private static readonly TimeSpan MaxTradeAge = TimeSpan.FromMinutes(30);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public MarketStatus Status() => NyseCalendar.StatusAt(Now);

    public MarketStatus StatusFor(ExchangeSession? session) => session is null ? Status() : ForeignStatus(session, Now);

    public string? WhyCannotFill(Quote quote)
    {
        // Always worked out from the clock, as the quote may have been fetched just before the bell.
        var now = Now;
        var status = quote.Session is null ? NyseCalendar.StatusAt(now) : ForeignStatus(quote.Session, now);

        if (!status.IsOpen) return $"{status.Label}. {status.Detail}.";

        // Just after the open, the feed still shows yesterday's close. Filling on that would hand out the overnight move for free.
        if (quote.PriceTime is not { } priceTime || priceTime < status.SessionStart)
            return $"{quote.Symbol} hasn't traded yet this session, so there's no current price to fill at.";

        if (now - priceTime > MaxTradeAge)
            return $"{quote.Symbol} hasn't traded for over {MaxTradeAge.TotalMinutes:0} minutes, so its last price may be out of date.";

        return null;
    }

    private static MarketStatus ForeignStatus(ExchangeSession session, DateTime now)
    {
        if (now >= session.Start && now < session.End)
            return new(MarketPhase.Open, "Market open", $"Closes at {LocalTime(session, session.End)}", null, session.End, session.Start);

        // Yahoo reports the current or next session, so a past one means the next isn't known yet.
        return now < session.Start
            ? new(MarketPhase.Overnight, "Market closed", $"Opens {LocalTime(session, session.Start)}", session.Start, null, null)
            : new(MarketPhase.Overnight, "Market closed", "Opens next trading day", null, null, session.Start);
    }

    private static string LocalTime(ExchangeSession session, DateTime utc)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(session.TimeZoneName, out var zone)) return $"{utc:MMM d, HH:mm} UTC";
        return $"{TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture)} ({session.TimeZoneName})";
    }
}

/// <summary>For the simulated feed, which exists so orders can be tried at any hour.</summary>
public sealed class AlwaysOpenClock : IMarketClock
{
    public static readonly MarketStatus Open = new(MarketPhase.Open, "Simulated market", "Always open. Prices aren't real", null, null, null);

    public MarketStatus Status() => Open;
    public MarketStatus StatusFor(ExchangeSession? session) => Open;
    public string? WhyCannotFill(Quote quote) => null;
}
