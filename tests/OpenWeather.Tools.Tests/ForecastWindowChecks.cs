using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Abstractions.Registration;
using Voxta.Modules.Aios.OpenWeather.ChatAugmentations;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.OpenWeather.Helper;
using Voxta.Modules.Aios.OpenWeather.Tools;
using Voxta.Modules.Aios.Tools;

internal static class ForecastWindowChecks
{
    public static async Task RunAsync()
    {
        using var transport = new Transport();
        using var http = new HttpClient(transport);
        var client = new OpenWeatherClient(http, "test-key", NullLogger<OpenWeatherClient>.Instance);
        var settings = new OpenWeatherChatAugmentationsSettings
        { MyLocation = "Berlin, DE", Units = "imperial", WeatherDetails = ["Temp"], PollutionDetails = ["AQI"], TileCachePath = "unused" };
        var catalog = OpenWeatherToolCatalog.Create(new(client, settings, CultureInfo.InvariantCulture, (_, _, _) => Task.CompletedTask),
            OpenWeatherToolsSettings.From(new StaticSettingsSource(new())), NullLogger.Instance);
        async Task<string> Call(string operation, object arguments)
        {
            var count = transport.Requests;
            var result = await catalog.CallToolAsync(operation, JsonSerializer.SerializeToElement(arguments), default);
            Check(!result.IsError, result.Text);
            Check(transport.Requests == count + 2, "One geocoding plus one data call; no additional requests for time-zone lookup");
            return result.Text;
        }
        foreach (var op in new[] { "get_weather_forecast", "get_air_pollution_forecast" })
        {
            var morning = await Call(op, new { date = "2026-10-29", period = "morning" });
            Check(morning.Contains("Requested window: 2026-10-29 06:00 +01:00 to 2026-10-29 12:00 +01:00"), "Date/period resolve automatically in destination time");
            var fullDay = await Call(op, new { date = "2026-10-29" });
            Check(fullDay.Contains("to 2026-10-30 00:00 +01:00"), "Date alone selects full civil day");
            var calls = transport.Requests;
            foreach (var invalid in new object[] { new { period = "morning" }, new { date = "2026-02-30" },
                new { date = "2026-10-29", period = "lunchtime" }, new { date = "tomorrow", start = "tomorrow", end = "2026-10-31" } })
                Check((await catalog.CallToolAsync(op, JsonSerializer.SerializeToElement(invalid), default)).IsError, "Invalid/mixed date selection rejected");
            Check(calls == transport.Requests, "Invalid date/period adds no provider requests");
        }
        var text = await Call("get_weather_forecast", new { start = "2026-10-29T10:00", end = "2026-10-29T13:00", details = "Humidity,WindGust,PrecipitationProbability,Precipitation" });
        var rows = text.Split('\n').Where(line => line.StartsWith("2026-", StringComparison.Ordinal)).ToArray();
        Check(rows.Length == 3 && rows[0].Contains("07:00 +01:00 [surrounding context]") && rows[1].Contains("10:00 +01:00:")
            && rows[2].Contains("13:00 +01:00 [surrounding context]"), "Individual three-hour entries, exclusive end and one context entry each side");
        Check(rows[1].Contains("Humidity: 13%") && rows[1].Contains("mph") && rows[1].Contains("30%") && rows[1].Contains("mm/3h")
            && !text.Contains("Temperature:") && !text.Contains(" Day:"), "Explicit fields, units, accumulation semantics and no averaging");
        text = await Call("get_weather_forecast", new { start = "2026-10-29T11:00", end = "2026-10-29T12:00", details = "Humidity" });
        Check(text.Contains("no forecast timestamp inside") && text.Split('\n').Count(line => line.StartsWith("2026-")) == 2, "Narrow gap returns surrounding data, never invented exact-hour values");
        var explicitUtc = await Call("get_weather_forecast", new { start = "2026-10-29T10:00Z", end = "2026-10-29T11:00Z", details = "Humidity" });
        Check(explicitUtc.Contains("Requested window: 2026-10-29 11:00 +01:00 to 2026-10-29 12:00 +01:00"), "UTC input converted to destination time");
        text = await Call("get_air_pollution_forecast", new { start = "2026-10-29T10:00", end = "2026-10-29T12:00", details = "NH3" });
        rows = text.Split('\n').Where(line => line.StartsWith("2026-")).ToArray();
        Check(rows.Length == 4 && rows[0].Contains("09:00 +01:00 [surrounding context]") && rows[^1].Contains("12:00 +01:00 [surrounding context]")
            && text.Contains("Resolution: 1-hour steps") && text.Contains("NH₃:") && !text.Contains("AQI"), "Pollution hourly detail override");
        text = await Call("get_weather_forecast", new { start = "2026-10-28", end = "2026-10-29T04:00", details = "Humidity" });
        Check(text.Contains("bounds extend beyond") && text.Contains("only the listed times"), "Partial coverage is explicit");
        text = await Call("get_weather_forecast", new { start = "2026-10-29", end = "2026-10-30", details = "Temp" });
        Check(text.Contains("Requested window: 2026-10-29 00:00 +01:00 to 2026-10-30 00:00 +01:00"), "Date-only endpoints are destination midnight");
        foreach (var op in new[] { "get_weather_forecast", "get_air_pollution_forecast" })
        {
            var count = transport.Requests;
            foreach (var arguments in new object[] { new { start = "tomorrow" }, new { end = "tomorrow" },
                new { start = "2026-02-30", end = "2026-03-01" }, new { start = "tomorrowT25:00", end = "tomorrowT26:00" },
                new { start = "next week", end = "later" }, new { start = 12, end = 13 } })
                Check((await catalog.CallToolAsync(op, JsonSerializer.SerializeToElement(arguments), default)).IsError, "Invalid forecast request");
            Check(transport.Requests == count, "Malformed range is rejected before network requests");
            foreach (var arguments in new[] {
                new { start = "2030-01-01", end = "2030-01-02" }, new { start = "2020-01-01", end = "2020-01-02" },
                new { start = "2026-10-29T12:00", end = "2026-10-29T10:00" } })
            {
                var result = await catalog.CallToolAsync(op, JsonSerializer.SerializeToElement(arguments), default);
                Check(result.IsError && !result.Text.Contains("Humidity:"), "Out-of-range and reversed windows never fall back to broad data");
            }
        }
        text = await Call("get_weather_forecast", new { });
        Check(text.Contains(" Day:") && !text.Contains("Requested window:") && !text.Contains("Humidity:"), "Omitted range retains default weather summary");
        text = await Call("get_air_pollution_forecast", new { });
        Check(text.Contains("UTC day/night averages") && text.Contains("AQI") && !text.Contains("NH₃:"), "Omitted range retains default pollution summary");

        var geo = new GeoResult { Name = "Berlin", Country = "DE", Lat = 52.52, Lon = 13.405 };
        var now = DateTimeOffset.Parse("2026-10-28T23:30:00Z", CultureInfo.InvariantCulture);
        var relative = ForecastWindowRequest.Parse("tomorrowT06:00", "tomorrowT12:00")!.Resolve(geo, now);
        Check(relative.Format(relative.Start).StartsWith("2026-10-30 06:00"), "Tomorrow uses destination current date");
        var autumn = ForecastWindowRequest.Parse("2026-10-25", "2026-10-26")!.Resolve(geo, now);
        var spring = ForecastWindowRequest.Parse("2026-03-29", "2026-03-30")!.Resolve(geo, now);
        Check((autumn.End - autumn.Start).TotalHours == 25 && (spring.End - spring.Start).TotalHours == 23, "DST civil-day lengths");
        foreach (var ambiguous in new[] { "2026-10-25T02:30", "2026-03-29T02:30" })
        {
            try { ForecastWindowRequest.Parse(ambiguous, "2026-10-26")!.Resolve(geo, now); throw new Exception("DST input accepted silently"); }
            catch (ModuleToolInputException ex) { Check(ex.Message.Contains("explicit UTC offset"), "Ambiguous/skipped local times need clarification"); }
        }
        var civilDay = ForecastWindowRequest.Parse(null, null, "2026-10-25")!.Resolve(geo, now);
        var night = ForecastWindowRequest.Parse(null, null, "2026-10-24", "night")!.Resolve(geo, now);
        Check((civilDay.End - civilDay.Start).TotalHours == 25 && (night.End - night.Start).TotalHours == 9, "Date/period advances civil date across DST");
        foreach (var (period, startHour, endHour) in new[] { ("day", 0, 0), ("morning", 6, 12), ("afternoon", 12, 18), ("evening", 18, 22), ("night", 22, 6) })
        {
            var window = ForecastWindowRequest.Parse(null, null, "tomorrow", period)!.Resolve(geo, now);
            Check(window.Start.Hour == startHour && window.End.Hour == endHour && window.Start.Day == 30, "Named period: " + period);
        }
        var repeated = ForecastWindowRequest.Parse("2026-10-25T02:00+02:00", "2026-10-25T03:00+01:00")!.Resolve(geo, now);
        var points = new[] { repeated.Start.ToUnixTimeSeconds(), repeated.Start.AddHours(1).ToUnixTimeSeconds() };
        Check(repeated.Format(DateTimeOffset.FromUnixTimeSeconds(points[0])).Contains("02:00 +02:00")
            && repeated.Format(DateTimeOffset.FromUnixTimeSeconds(points[1])).Contains("02:00 +01:00"), "Repeated DST hour stays distinguishable by offset");
        var gapped = new[] { relative.Start.ToUnixTimeSeconds(), relative.Start.AddHours(5).ToUnixTimeSeconds() };
        Check(relative.Describe(gapped, relative.Select(gapped, x => x), x => x, 1).Contains("gaps"), "Missing provider entries are explicit");
        Console.WriteLine("PASS targeted forecast windows, individual samples, context, coverage, defaults, input validation, local/UTC dates and DST");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Transport : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            var first = DateTimeOffset.Parse("2026-10-29T00:00:00Z", CultureInfo.InvariantCulture);
            var geo = new GeoResult { Name = "Berlin", Country = "DE", Lat = 52.52, Lon = 13.405 };
            object data;
            if (request.RequestUri!.AbsolutePath.Contains("/geo/")) data = new[] { geo };
            else if (request.RequestUri.AbsolutePath.Contains("air_pollution"))
                data = new OpenWeatherAirPollutionResponse { List = Enumerable.Range(0, 25).Select(i => new AirPollutionData
                { Dt = first.AddHours(i).ToUnixTimeSeconds(), Main = new() { Aqi = 2 }, Components = new() { Nh3 = i } }).ToList() };
            else data = new OpenWeatherForecastResponse
            {
                City = new() { Coord = new() { Lat = 52.52, Lon = 13.405 }, Country = "DE", Timezone = 0 }, // Deliberately not Berlin's offset.
                List = Enumerable.Range(0, 9).Select(i => new ForecastItem
                {
                    Dt = first.AddHours(i * 3).ToUnixTimeSeconds(), Main = new() { Temp = 50 + i, FeelsLike = 49 + i, TempMin = 48 + i, TempMax = 52 + i, Humidity = 10 + i },
                    Weather = [new() { Description = "clear", Icon = "01d" }], Wind = new() { Speed = 2 + i, Gust = 3 + i },
                    Clouds = new() { All = 10 }, Sys = new(), Pop = i / 10.0, Rain = new() { ThreeHours = i },
                }).ToList(),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(data), System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
