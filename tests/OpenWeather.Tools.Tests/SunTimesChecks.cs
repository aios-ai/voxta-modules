using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Abstractions.Registration;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.OpenWeather.ChatAugmentations;
using Voxta.Modules.Aios.OpenWeather.Helper;
using Voxta.Modules.Aios.OpenWeather.Tools;
using Voxta.Modules.Aios.Tools;

internal static class SunTimesChecks
{
    public static async Task RunAsync()
    {
        var now = DateTimeOffset.Parse("2026-10-28T23:30:00Z", CultureInfo.InvariantCulture);
        GeoResult Location(double lat, double lon) => new() { Name = "Test", Country = "DE", Lat = lat, Lon = lon };
        var berlin = Location(52.52, 13.405);
        var result = SunTimes.Calculate(berlin, "2026-10-29", now);
        // Independent USNO reference: /api/rstt/oneday?date=2026-10-29&coords=52.52,13.405&tz=1
        // Rise 06:57, set 16:42. Allow different standard refraction models and rounding.
        Check(result.Sunrise is { } rise && Math.Abs((rise.TimeOfDay - new TimeSpan(6, 57, 0)).TotalMinutes) < 2, "Berlin sunrise matches USNO reference");
        Check(result.Sunset is { } set && Math.Abs((set.TimeOfDay - new TimeSpan(16, 42, 0)).TotalMinutes) < 2, "Berlin sunset matches USNO reference");
        Check(result.TimeZone == "Europe/Berlin" && result.Sunrise!.Value.Offset == TimeSpan.FromHours(1), "Destination zone");
        Check(SunTimes.Calculate(berlin, "today", now).Date == new DateOnly(2026, 10, 29)
            && SunTimes.Calculate(berlin, "tomorrow", now).Date == new DateOnly(2026, 10, 30), "Relative dates use destination date, not UTC or host date");
        var before = SunTimes.Calculate(berlin, "2026-10-24", now);
        var after = SunTimes.Calculate(berlin, "2026-10-25", now);
        Check(before.Sunrise!.Value.Offset == TimeSpan.FromHours(2) && after.Sunrise!.Value.Offset == TimeSpan.FromHours(1)
            && (before.Sunrise.Value.TimeOfDay - after.Sunrise.Value.TimeOfDay).TotalMinutes > 50, "DST transition uses event-day offset");
        var polar = Location(69.6492, 18.9553);
        foreach (var date in new[] { "2026-06-21", "2026-12-21" })
        {
            var times = SunTimes.Calculate(polar, date, now);
            Check(times.Sunrise == null && times.Sunset == null && times.Describe("Tromso").Contains("no event on this local date"), "Polar day/night has no invented events");
        }
        var pacific = SunTimes.Calculate(Location(1.8721, -157.4278), "2026-10-29", now);
        Check(pacific.Sunrise!.Value.Date == new DateTime(2026, 10, 29) && pacific.Sunrise.Value.Offset == TimeSpan.FromHours(14)
            && pacific.Sunrise.Value.UtcDateTime.Date == new DateTime(2026, 10, 28), "UTC date differs across international date line");
        var fake = System.Reflection.DispatchProxy.Create<IOpenWeatherClient, WeatherClientProxy>();
        var proxy = (WeatherClientProxy)fake;
        var settings = new OpenWeatherChatAugmentationsSettings { MyLocation = "Berlin, DE", TileCachePath = "unused" };
        var tools = OpenWeatherToolCatalog.Create(new(fake, settings, CultureInfo.InvariantCulture, (_, _, _) => Task.CompletedTask),
            OpenWeatherToolsSettings.From(new StaticSettingsSource(new())), NullLogger.Instance);
        async Task<Voxta.Abstractions.Services.Agents.ChatToolCallResult> Call(string tool, object args) =>
            await tools.CallToolAsync(tool, System.Text.Json.JsonSerializer.SerializeToElement(args), default);
        foreach (var date in new[] { "2026-02-30", "next week", "2100-01-01", "" })
            Check((await Call("get_sun_times", new { date })).IsError, "Invalid date");
                Check(proxy.Calls.Count == 0, "Invalid dates rejected before lookup");
        var response = await Call("get_sun_times", new { });
        Check(!response.IsError, "Omitted sun date defaults to today");
        proxy.Calls.Clear();
        response = await Call("get_sun_times", new { date = "2026-10-29" });
        Check(!response.IsError && response.Text.Contains("2026-10-29 06:5") && response.Text.Contains("Astronomical estimates")
            && proxy.Calls.SequenceEqual(new[] { "ResolveLocationAsync" }), "One geocoding request, local calculation, honest provenance");
        response = await Call("get_weather", new { when = "forecast", details = "Wind,Sunrise" });
        Check(!response.IsError && response.Text.Contains("Use get_sun_times") && !response.Text.Contains("City sunrise:") && !response.Text.Contains(": . Wind"), "Forecast routes future sun queries without today's misleading metadata or broken punctuation");
        response = await Call("get_weather", new { when = "forecast", details = "Sunrise" });
        Check(!response.IsError && response.Text.Contains("Use get_sun_times") && !response.Text.Contains(" Day:") && !response.Text.Contains(" Night:"), "Sun-only forecast does not print empty weather periods");
        response = await Call("get_weather", new { details = "Sunrise" });
        Check(response.Text.Contains("only to their displayed date") && response.Text.Contains("get_sun_times"), "Current result explains date boundary");
        Console.WriteLine("PASS date-specific sun times, independent reference, DST, polar dates, international date line and tool routing");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
