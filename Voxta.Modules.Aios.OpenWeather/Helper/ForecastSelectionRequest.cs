using System.Globalization;
using System.Text.RegularExpressions;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.OpenWeather.Helper;

// Calendar selection is independent of provider coverage. Never move a requested date into the forecast horizon.
internal sealed record ForecastSelectionRequest(
    string Date, string? Period, string? Range, int? Count, ForecastWindowRequest? Exact = null)
{
    private static readonly string[] Weekdays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    public static ForecastSelectionRequest? Parse(string? start, string? end, string? date = null,
        string? period = null, string? range = null, int? count = null)
    {
        if (start != null || end != null)
        {
            if (date != null || period != null || range != null || count != null)
                throw new ModuleToolInputException("Use start/end OR date/period/range/count, not both.");
            return new("today", null, null, null, ForecastWindowRequest.Parse(start, end));
        }
        if (date == null && period == null && range == null && count == null) return null;
        date = date?.ToLowerInvariant();
        period = period?.ToLowerInvariant();
        range = range?.ToLowerInvariant();
        if (range is not (null or "hours" or "days" or "weekend" or "next_weekend" or "this_week" or "next_week"))
            throw new ModuleToolInputException("range must be hours, days, weekend, next_weekend, this_week or next_week.");
        if (range is "hours" or "days")
        {
            var limit = range == "hours" ? 744 : 31;
            if (count is null || count < 1 || count > limit)
                throw new ModuleToolInputException($"range={range} requires an integer count from 1 to {limit}. This does not extend the provider's forecast horizon.");
        }
        else if (count != null) throw new ModuleToolInputException("count is accepted only with range=hours or range=days.");
        if (range == "hours" && (date != null || period != null))
            throw new ModuleToolInputException("range=hours starts now; omit date and period.");
        if (range is "weekend" or "next_weekend" or "this_week" or "next_week" && date != null)
            throw new ModuleToolInputException("Calendar ranges determine their own dates; omit date or use range=days.");
        if (date != null) ValidateDate(date);
        _ = PeriodHours(period);
        return new(date ?? "today", period, range, count);
    }

    private static void ValidateDate(string date)
    {
        var weekday = date.StartsWith("next_", StringComparison.Ordinal) ? date[5..] : date;
        if (date is "today" or "tomorrow" or "day_after_tomorrow" || Weekdays.Contains(weekday)) return;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed.Year is < 1901 or > 2099)
            throw new ModuleToolInputException("date must be today, tomorrow, day_after_tomorrow, a weekday (monday..sunday), next_<weekday>, or YYYY-MM-DD (1901-2099).");
    }

    private static (TimeOnly Start, TimeOnly End, bool Overnight) PeriodHours(string? period)
    {
        var hours = period switch
        {
            null or "all_day" or "day" => (0, 0), // Retain beta.7's day alias.
            "morning" => (6, 12),
            "afternoon" => (12, 18),
            "evening" => (18, 22),
            "night" => (22, 6),
            _ => (-1, -1),
        };
        if (hours.Item1 >= 0) return (new(hours.Item1, 0), new(hours.Item2, 0), hours.Item2 <= hours.Item1);
        if (period != null && Regex.IsMatch(period, @"^[0-9]{2}:[0-9]{2}-[0-9]{2}:[0-9]{2}$")
            && TimeOnly.TryParseExact(period[..5], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            && TimeOnly.TryParseExact(period[6..], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            && start != end)
            return (start, end, end < start);
        throw new ModuleToolInputException("period must be all_day, morning, afternoon, evening, night or HH:mm-HH:mm (00:00-23:59). Overnight periods end the next day; identical clock times are invalid, use all_day.");
    }

    public ForecastSelection Resolve(GeoResult? location, DateTimeOffset utcNow)
    {
        if (Exact != null) return new([Exact.Resolve(location, utcNow)]);
        var zone = LocationTimeZone.Resolve(location);
        if (Range == "hours") return new([new(utcNow, utcNow.AddHours(Count!.Value), zone)]);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);
        var first = Date switch
        {
            "today" => today,
            "tomorrow" => today.AddDays(1),
            "day_after_tomorrow" => today.AddDays(2),
            _ => ResolveDate(Date, today),
        };
        var days = Range == "days" ? Count!.Value : 1;
        if (Range is "weekend" or "next_weekend")
        {
            // On Sunday include the current weekend, then discard its elapsed Saturday.
            first = today.AddDays(today.DayOfWeek == DayOfWeek.Sunday ? -1 : (6 - (int)today.DayOfWeek + 7) % 7);
            if (Range == "next_weekend") first = first.AddDays(7);
            days = 2;
        }
        else if (Range is "this_week" or "next_week")
        {
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            first = Range == "next_week" ? monday.AddDays(7) : today;
            days = Range == "next_week" ? 7 : 7 - ((int)today.DayOfWeek + 6) % 7;
        }
        var hours = PeriodHours(Period);
        var windows = new List<ForecastWindow>();
        var elapsed = 0;
        for (var i = 0; i < days; i++)
        {
            var date = first.AddDays(i);
            var localEnd = (hours.Overnight ? date.AddDays(1) : date).ToDateTime(hours.End);
            // Entirely elapsed periods need no DST disambiguation and must never move to a future date.
            if (localEnd <= TimeZoneInfo.ConvertTime(utcNow, zone).DateTime)
            {
                elapsed++;
                continue;
            }
            var start = Local(date.ToDateTime(hours.Start), zone);
            var end = Local(localEnd, zone);
            if (start < utcNow) start = utcNow;
            if (end <= start) throw new ModuleToolInputException("The selected local period contains no future time.");
            windows.Add(new(start, end, zone));
        }
        if (windows.Count == 0)
            throw new ModuleToolInputException($"The selected period has already ended in {zone.Id} ({first:yyyy-MM-dd}, {Period ?? "all_day"}). No future forecast period remains; do not substitute tomorrow or another period.");
        return new(windows, elapsed);
    }

    private static DateOnly ResolveDate(string date, DateOnly today)
    {
        var next = date.StartsWith("next_", StringComparison.Ordinal);
        var index = Array.IndexOf(Weekdays, next ? date[5..] : date);
        return index >= 0 ? today.AddDays((index - (int)today.DayOfWeek + 7) % 7 + (next ? 7 : 0))
            : DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset Local(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new ModuleToolInputException($"{local:yyyy-MM-ddTHH:mm} is a skipped or ambiguous local time in {zone.Id}. Use start/end with an explicit UTC offset to identify a real instant.");
        return new(local, zone.GetUtcOffset(local));
    }
}

internal sealed record ForecastSelection(IReadOnlyList<ForecastWindow> Windows, int ElapsedPeriods = 0);
