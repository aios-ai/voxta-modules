using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Abstractions.Registration;
using Voxta.Modules.Aios.OpenWeather.ChatAugmentations;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.OpenWeather.Helper;
using Voxta.Modules.Aios.OpenWeather.Tools;
using Voxta.Modules.Aios.Tools;

internal static class WeatherWhenChecks
{
    public static async Task RunAsync()
    {
        var geo = new GeoResult { Name = "Berlin", Country = "DE", Lat = 52.52, Lon = 13.405 };
        var now = DateTimeOffset.Parse("2026-10-26T09:30Z", CultureInfo.InvariantCulture);
        ForecastSelection Resolve(string text) => WeatherWhen.Parse(text).Selection!.Resolve(geo, now);
        Check(WeatherWhen.Parse(null).Current && WeatherWhen.Parse(" NOW ").Current, "Omitted/now selects current observations");
        Check(!WeatherWhen.Parse("forecast").Current && WeatherWhen.Parse("forecast").Selection == null
            && WeatherWhen.Parse("next few days").Selection == null, "Explicit general forecast uses available summary");
        foreach (var (text, date, period) in new[] {
            ("tomorrow morning", 27, "morning"), ("day after tomorrow", 28, "all_day"),
            ("the day after tomorrow", 28, "all_day"), ("day_after_tomorrow", 28, "all_day"),
            ("Saturday morning", 31, "morning"), ("  THURSDAY   EVENING ", 29, "evening"),
            ("next_monday", 2, "all_day"), ("tomorrow 10:00-18:00", 27, "10:00-18:00") })
        {
            var selection = Resolve(text);
            var expected = ForecastSelectionRequest.Parse(null, null,
                text.Contains("next_monday") ? "next_monday" : $"2026-{(date == 2 ? "11" : "10")}-{date:00}", period)!.Resolve(geo, now);
            Check(selection.Windows.SequenceEqual(expected.Windows), "Controlled phrase resolves deterministically: " + text);
        }
        var rest = Resolve("rest of the day").Windows.Single();
        Check(rest.Start == now && rest.End.Day == 27 && rest.End.Hour == 0, "Rest of day clips elapsed time");
        var hours = Resolve("next 5 hours").Windows.Single();
        Check(hours.Start == now && hours.End - hours.Start == TimeSpan.FromHours(5), "Next hours use elapsed time");
        var afternoons = Resolve("next 3 afternoons").Windows;
        Check(afternoons.Count == 3 && afternoons.All(w => w.Start.Hour == 12 && w.End.Hour == 18)
            && afternoons[0].End < afternoons[1].Start, "Repeated periods stay disjoint");
        Check(Resolve("3 days from tomorrow").Windows.First().Start.Day == 27
            && Resolve("3 days from tomorrow").Windows.Last().End.Day == 30, "Counted dates start from the named day");
        Check(Resolve("tomorrow and the day after tomorrow").Windows.Count == 2, "Common two-day expression");
        Check(Resolve("this weekend").Windows.First().Start.Day == 31 && Resolve("next weekend").Windows.First().Start.Day == 7,
            "Weekends retain agreed meaning");
        Check(Resolve("this week").Windows.Count == 7 && Resolve("next week").Windows.First().Start.Day == 2, "Week boundaries are local Mondays");
        var inclusive = Resolve("2026-10-27 to 2026-10-29").Windows;
        Check(inclusive.Count == 3 && inclusive[^1].End.Day == 30, "Date-range end is inclusive, internal windows remain exclusive");
        Check(Resolve("2026-10-27 to 2026-10-29 afternoon").Windows.All(w => w.Start.Hour == 12 && w.End.Hour == 18), "Date-range period filter");
        var overnight = Resolve("saturday 23:00-01:00").Windows.Single();
        Check(overnight.Start.Day == 31 && overnight.End.Day == 1 && overnight.End.Month == 11, "Custom overnight period crosses the month boundary");
        var exact = Resolve("2026-10-27T10:00Z/2026-10-27T12:00Z").Windows.Single();
        Check(exact.Start == DateTimeOffset.Parse("2026-10-27T10:00Z", CultureInfo.InvariantCulture), "ISO interval preserves explicit instants");
        var dstNow = DateTimeOffset.Parse("2026-10-24T12:00Z", CultureInfo.InvariantCulture);
        var dstDay = WeatherWhen.Parse("tomorrow").Selection!.Resolve(geo, dstNow).Windows.Single();
        Check((dstDay.End - dstDay.Start).TotalHours == 25, "Phrase parser retains civil-day DST rules");
        var summary = WeatherForecastSummariser.Summarise(new[] { "2026-10-24T06:00Z", "2026-10-25T06:00Z" }.Select(date =>
            new ForecastItem { Dt = DateTimeOffset.Parse(date, CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
                Main = new() { Temp = 12, FeelsLike = 12, TempMin = 12, TempMax = 12 }, Wind = new(), Clouds = new(), Sys = new(),
                Weather = [new() { Description = "clear", Icon = "01d" }] }).ToList(),
            CultureInfo.InvariantCulture, new HashSet<string> { "Temp" }, timeZone: LocationTimeZone.Resolve(geo));
        Check(summary.Contains("2026-10-24 Saturday Day:") && summary.Contains("2026-10-25 Sunday Night:"),
            "General summaries use each forecast date's DST rule rather than today's fixed offset");
        foreach (var invalid in new[] { "", "maybe soon", "next 0 hours", "next 32 days", "next 745 hours", "next 5 hours morning",
            "2026-02-30", "tomorrow 10:00-10:00", "2026-11-02 to 2026-11-01", "next 999999999999999999 days" })
        {
            try { WeatherWhen.Parse(invalid); throw new Exception("Invalid when accepted: " + invalid); }
            catch (ModuleToolInputException) { }
        }

        var client = DispatchProxy.Create<IOpenWeatherClient, WeatherClientProxy>();
        var fake = (WeatherClientProxy)client;
        var settings = new OpenWeatherChatAugmentationsSettings
        { MyLocation = "Berlin, DE", WeatherDetails = ["Temp"], PollutionDetails = ["AQI"], TileCachePath = "unused" };
        var catalog = OpenWeatherToolCatalog.Create(new(client, settings, CultureInfo.InvariantCulture, (_, _, _) => Task.CompletedTask),
            OpenWeatherToolsSettings.From(new StaticSettingsSource(new())), NullLogger.Instance);
        async Task<string> Call(string tool, object args, string expectedMethod)
        {
            fake.Calls.Clear();
            var result = await catalog.CallToolAsync(tool, JsonSerializer.SerializeToElement(args), default);
            Check(!result.IsError && fake.Calls.SequenceEqual(new[] { expectedMethod }), "One selected endpoint: " + tool + " " + result.Text);
            return result.Text;
        }
        var report = await Call("get_weather", new { details = "PrecipitationProbability" }, "FetchWeatherData");
        Check(report.Contains("Rain: 2") && report.Contains("mm/h") && report.Contains("Substitution:") && report.Contains("not a probability")
            && !report.Contains("%") && !report.Contains("current temperature"), "Explicit probability request gets labelled precipitation replacement only");
        await Call("get_weather", new { when = "now", details = "PrecipitationProbability,Precipitation" }, "FetchWeatherData");
        report = await Call("get_weather", new { }, "FetchWeatherData");
        Check(!report.Contains("Substitution:") && !report.Contains("Rain:"), "Substitution never mutates configured defaults");
        report = await Call("get_weather", new { when = "forecast", details = "PrecipitationProbability" }, "FetchForecastData");
        Check(!report.Contains("Substitution:"), "Forecast probability is never replaced with volume");
        await Call("get_air_pollution", new { }, "FetchAirPollutionData");
        await Call("get_air_pollution", new { when = "now" }, "FetchAirPollutionData");
        await Call("get_air_pollution", new { when = "forecast" }, "FetchAirPollutionForecastData");
        foreach (var json in new[] { "{\"when\":\"maybe soon\"}", "{\"when\":42}", "{\"date\":\"tomorrow\"}", "{\"start\":\"tomorrow\"}" })
        {
            fake.Calls.Clear();
            Check((await catalog.CallToolAsync("get_weather", JsonSerializer.Deserialize<JsonElement>(json), default)).IsError
                && fake.Calls.Count == 0, "Unsupported/removed arguments cannot silently select current weather: " + json);
        }
        fake.Calls.Clear();
        foreach (var old in new[] { "get_weather_forecast", "get_air_pollution_forecast" })
            Check((await catalog.CallToolAsync(old, JsonSerializer.SerializeToElement(new { }), default)).IsError, "Old tool is not callable");
        Check(fake.Calls.Count == 0 && catalog.GetTools().Count == 5, "Only five tools are exposed");
        Console.WriteLine("PASS controlled when grammar, combined endpoint routing, honest precipitation substitution and removed-argument validation");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
