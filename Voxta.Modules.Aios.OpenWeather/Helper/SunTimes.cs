using System.Globalization;
using CosineKitty;
using GeoTimeZone;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.OpenWeather.Helper;

internal sealed record SunTimes(DateOnly Date, string TimeZone, DateTimeOffset? Sunrise, DateTimeOffset? Sunset)
{
    public static void ValidateDate(string? date)
    {
        if (date is "today" or "tomorrow") return;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed.Year < 1901 || parsed.Year > 2099)
            throw new ModuleToolInputException("date must be today, tomorrow, or YYYY-MM-DD between 1901 and 2099. It is the date at the requested location.");
    }

    public static SunTimes Calculate(GeoResult location, string date, DateTimeOffset utcNow)
    {
        ValidateDate(date);
        if (!double.IsFinite(location.Lat) || !double.IsFinite(location.Lon) || Math.Abs(location.Lat) > 90 || Math.Abs(location.Lon) > 180)
            throw new ModuleToolInputException("Sun times require valid location coordinates.");
        var zoneId = TimeZoneLookup.GetTimeZone(location.Lat, location.Lon).Result;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone))
            throw new ModuleToolInputException("The destination time zone is unavailable on this system; date-specific local sun times cannot be determined reliably.");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);
        var requested = date switch
        {
            "today" => today,
            "tomorrow" => today.AddDays(1),
            _ => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        if (requested.Year < 1901 || requested.Year > 2099)
            throw new ModuleToolInputException("Calculated sun times support dates between 1901 and 2099.");

        var start = StartOfDay(requested, zone);
        var end = StartOfDay(requested.AddDays(1), zone);
        var observer = new Observer(location.Lat, location.Lon, 0);
        DateTimeOffset? Event(Direction direction)
        {
            var found = Astronomy.SearchRiseSet(Body.Sun, observer, direction, new AstroTime(start.UtcDateTime), (end - start).TotalDays);
            if (found == null) return null;
            var utc = new DateTimeOffset(found.ToUtcDateTime());
            // Search bounds are inclusive in the library; our local calendar day is half-open.
            return utc >= start && utc < end ? TimeZoneInfo.ConvertTime(utc, zone) : null;
        }
        return new(requested, zoneId, Event(Direction.Rise), Event(Direction.Set));
    }

    private static DateTimeOffset StartOfDay(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Some zones change clocks at midnight. Find the first real instant of that local date.
        while (zone.IsInvalidTime(local) && DateOnly.FromDateTime(local) == date) local = local.AddMinutes(1);
        if (DateOnly.FromDateTime(local) != date)
            throw new ModuleToolInputException("This local calendar date does not exist in the destination time zone.");
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new(local, offset);
    }

    public string Describe(string location)
    {
        static string Format(DateTimeOffset? time) => time?.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) ?? "no event on this local date";
        return $"Calculated sun times for {location} on {Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({TimeZone}). Sunrise: {Format(Sunrise)}. Sunset: {Format(Sunset)}. " +
            "Astronomical estimates for a sea-level unobstructed horizon, not OpenWeather observations. Terrain and atmospheric conditions can change visible times.";
    }
}
