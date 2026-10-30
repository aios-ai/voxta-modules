using System.Globalization;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.OpenWeather.Helper;
using Voxta.Modules.Aios.Tools;

internal static class ForecastSelectionChecks
{
    public static void Run()
    {
        var berlin = new GeoResult { Name = "Berlin", Country = "DE", Lat = 52.52, Lon = 13.405 };
        var now = Instant("2026-10-26T09:30Z"); // Monday 10:30 in Berlin.
        ForecastSelection Select(string? date = null, string? period = null, string? range = null, int? count = null,
            DateTimeOffset? at = null, GeoResult? location = null)
            => ForecastSelectionRequest.Parse(null, null, date, period, range, count)!.Resolve(location ?? berlin, at ?? now);
        void Bounds(ForecastWindow window, string start, string end)
            => Check(window.Start == Instant(start) && window.End == Instant(end), $"Wrong window: {window.Start:O} to {window.End:O}");
        Bounds(Select(date: "today").Windows.Single(), "2026-10-26T10:30+01:00", "2026-10-27T00:00+01:00");
        Bounds(Select(date: "tomorrow").Windows.Single(), "2026-10-27T00:00+01:00", "2026-10-28T00:00+01:00");
        Bounds(Select(date: "day_after_tomorrow").Windows.Single(), "2026-10-28T00:00+01:00", "2026-10-29T00:00+01:00");
        Bounds(Select(period: "morning").Windows.Single(), "2026-10-26T10:30+01:00", "2026-10-26T12:00+01:00");
        Bounds(Select(date: "saturday", period: "10:00-18:00").Windows.Single(), "2026-10-31T10:00+01:00", "2026-10-31T18:00+01:00");
        Bounds(Select(date: "saturday", period: "23:30-01:15").Windows.Single(), "2026-10-31T23:30+01:00", "2026-11-01T01:15+01:00");
        Bounds(Select(date: "saturday", period: "night").Windows.Single(), "2026-10-31T22:00+01:00", "2026-11-01T06:00+01:00");
        var twoDays = Select(date: "tomorrow", range: "days", count: 2).Windows;
        Check(twoDays.Count == 2 && twoDays[0].End == twoDays[1].Start, "Tomorrow plus the following day");
        Bounds(twoDays[1], "2026-10-28T00:00+01:00", "2026-10-29T00:00+01:00");
        var afternoons = Select(range: "days", count: 3, period: "afternoon").Windows;
        Check(afternoons.Count == 3 && afternoons.All(w => w.Start.Hour == 12 && w.End.Hour == 18)
            && afternoons[0].End < afternoons[1].Start, "Three afternoons are disjoint, not a continuous range");
        var remainder = Select(range: "days", count: 3, period: "morning", at: Instant("2026-10-26T15:00Z"));
        Check(remainder.ElapsedPeriods == 1 && remainder.Windows.Count == 2 && remainder.Windows[0].Start.Day == 27,
            "Elapsed morning is omitted without extending a three-day request by another day");
        ExpectInput(() => Select(date: "today", period: "morning", at: Instant("2026-10-26T15:00Z")), "already ended");
        ExpectInput(() => Select(date: "2026-10-01"), "already ended");

        var weekend = Select(range: "weekend").Windows;
        Check(weekend.Count == 2 && weekend[0].Start.Day == 31 && weekend[1].Start.Month == 11, "Monday resolves upcoming weekend across month boundary");
        var nextWeekend = Select(range: "next_weekend").Windows;
        Check(nextWeekend[0].Start.Day == 7 && nextWeekend[1].Start.Day == 8, "Next weekend advances the chosen weekend by seven days");
        var saturday = Instant("2026-10-31T12:00+01:00");
        Bounds(Select(range: "weekend", at: saturday).Windows[0], "2026-10-31T12:00+01:00", "2026-11-01T00:00+01:00");
        var sunday = Select(range: "weekend", at: Instant("2026-11-01T12:00+01:00"));
        Check(sunday.Windows.Count == 1 && sunday.Windows[0].Start.Day == 1 && sunday.Windows[0].End.Day == 2, "Sunday selects remaining current weekend");
        Check(Select(range: "next_weekend", at: Instant("2026-11-01T12:00+01:00")).Windows[0].Start.Day == 7,
            "Sunday next_weekend is the following Saturday");
        Check(Select(range: "this_week").Windows.Count == 7, "Monday this_week includes seven remaining calendar dates");
        var nextWeek = Select(range: "next_week").Windows;
        Check(nextWeek.Count == 7 && nextWeek[0].Start.Day == 2 && nextWeek[^1].End.Day == 9, "Next week is Monday through Sunday");
        var yearEnd = Select(range: "next_week", at: Instant("2026-12-31T12:00Z")).Windows;
        Check(yearEnd[0].Start.Year == 2027 && yearEnd[0].Start.Month == 1 && yearEnd[0].Start.Day == 4, "Next week crosses year boundary");

        var names = new[] { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };
        for (var day = 0; day < 7; day++)
        {
            var at = now.AddDays(day);
            var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, LocationTimeZone.Resolve(berlin)).DateTime);
            foreach (var name in names)
            {
                var selected = Select(date: name, at: at).Windows.Single();
                var following = Select(date: "next_" + name, at: at).Windows.Single();
                var offset = (int)(selected.Start.Date - localToday.ToDateTime(TimeOnly.MinValue)).TotalDays;
                Check(selected.Start.DayOfWeek.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)
                    && offset is >= 0 and <= 6 && following.Start.Date == selected.Start.Date.AddDays(7), "Weekday/next_weekday semantics: " + name);
            }
        }
        // Calendar days and overnight periods follow the destination's DST; elapsed hours do not grow/shrink.
        var beforeAutumn = Instant("2026-10-24T12:00Z");
        var civilDay = Select(date: "2026-10-25", at: beforeAutumn).Windows.Single();
        var civilNight = Select(date: "2026-10-24", period: "night", at: beforeAutumn).Windows.Single();
        Check((civilDay.End - civilDay.Start).TotalHours == 25 && (civilNight.End - civilNight.Start).TotalHours == 9, "Autumn civil day/night duration");
        var springDay = Select(date: "2026-03-29", at: Instant("2026-03-28T12:00Z")).Windows.Single();
        Check((springDay.End - springDay.Start).TotalHours == 23, "Spring civil day duration");
        var hours = Select(range: "hours", count: 5, at: Instant("2026-10-24T23:30Z")).Windows.Single();
        Check((hours.End - hours.Start).TotalHours == 5, "Next five elapsed hours across a repeated clock hour");
        ExpectInput(() => Select(date: "2026-10-25", period: "02:30-04:00", at: beforeAutumn), "explicit UTC offset");
        ExpectInput(() => Select(date: "2026-03-29", period: "02:30-04:00", at: Instant("2026-03-28T12:00Z")), "explicit UTC offset");
        var utcMidnight = Instant("2026-10-26T23:30Z");
        Bounds(Select(date: "tomorrow", at: utcMidnight).Windows.Single(), "2026-10-28T00:00+01:00", "2026-10-29T00:00+01:00");
        var newYork = new GeoResult { Name = "New York", Country = "US", Lat = 40.7128, Lon = -74.006 };
        var destinationTomorrow = Select(date: "tomorrow", at: Instant("2026-10-27T00:30Z"), location: newYork).Windows.Single();
        Check(destinationTomorrow.Start == Instant("2026-10-27T00:00-04:00"), "Date resolution uses destination, not host or UTC date");
        Check(ForecastSelectionRequest.Parse(null, null) == null, "No selection preserves available-summary behavior");
        foreach (var period in new[] { "", "25:00-26:00", "10:00-10:00", "24:00-01:00", "9:00-12:00" })
            ExpectInput(() => ForecastSelectionRequest.Parse(null, null, period: period), "period must be");
        foreach (var date in new[] { "", "2026-02-30", "next_today", "next_next_monday", "2100-01-01" })
            ExpectInput(() => ForecastSelectionRequest.Parse(null, null, date: date), "date must be");
        Console.WriteLine("PASS deterministic forecast dates, weekdays, weeks/weekends, disjoint periods, elapsed clipping, destination time zones and DST");
    }

    private static DateTimeOffset Instant(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void ExpectInput(Action action, string message)
    {
        try { action(); throw new Exception("Expected input error: " + message); }
        catch (ModuleToolInputException ex) { Check(ex.Message.Contains(message), ex.Message); }
    }
}
