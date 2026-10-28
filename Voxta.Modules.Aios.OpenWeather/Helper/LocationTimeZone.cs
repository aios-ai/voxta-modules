using GeoTimeZone;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.OpenWeather.Helper;

internal static class LocationTimeZone
{
    public static TimeZoneInfo Resolve(GeoResult? location)
    {
        if (location == null || !double.IsFinite(location.Lat) || !double.IsFinite(location.Lon)
            || Math.Abs(location.Lat) > 90 || Math.Abs(location.Lon) > 180)
            throw new ModuleToolInputException("Valid resolved coordinates are required to determine the destination time zone.");
        var zoneId = TimeZoneLookup.GetTimeZone(location.Lat, location.Lon).Result;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone))
            throw new ModuleToolInputException("The destination time zone is unavailable on this system; local dates and times cannot be determined reliably.");
        return zone;
    }
}
