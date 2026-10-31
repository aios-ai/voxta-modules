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
        var clock = new FixedClock(DateTimeOffset.Parse("2026-10-28T23:30:00Z", CultureInfo.InvariantCulture));
        var catalog = OpenWeatherToolCatalog.Create(new(client, settings, CultureInfo.InvariantCulture, (_, _, _) => Task.CompletedTask, timeProvider: clock),
            OpenWeatherToolsSettings.From(new StaticSettingsSource(new())), NullLogger.Instance);
        async Task<string> Call(string operation, object arguments)
        {
            var count = transport.Requests;
            var result = await catalog.CallToolAsync(operation, JsonSerializer.SerializeToElement(arguments), default);
            Check(!result.IsError, result.Text);
            Check(transport.Requests == count + 2, "One geocoding plus one data call; no additional requests for time-zone lookup");
            return result.Text;
        }
        foreach (var op in new[] { "get_weather", "get_air_pollution" })
        {
            var morning = await Call(op, new { when = "2026-10-29 morning" });
            Check(morning.Contains("Requested window: 2026-10-29 06:00 +01:00 to 2026-10-29 12:00 +01:00"), "Date/period resolve automatically in destination time");
            var fullDay = await Call(op, new { when = "2026-10-29" });
            Check(fullDay.Contains("to 2026-10-30 00:00 +01:00"), "Date alone selects full civil day");
            var calls = transport.Requests;
            foreach (var invalid in new object[] { new { when = "next 3 hours morning" }, new { when = "2026-02-30" },
                new { when = "2026-10-29 lunchtime" }, new { date = "tomorrow", start = "tomorrow", end = "2026-10-31" } })
                Check((await catalog.CallToolAsync(op, JsonSerializer.SerializeToElement(invalid), default)).IsError, "Invalid/mixed date selection rejected");
            Check(calls == transport.Requests, "Invalid date/period adds no provider requests");
        }
        var text = await Call("get_weather", new { when = "2026-10-29T10:00/2026-10-29T13:00", details = "Humidity,WindGust,PrecipitationProbability,Precipitation" });
        var rows = text.Split('\n').Where(line => line.StartsWith("2026-", StringComparison.Ordinal)).ToArray();
        Check(rows.Length == 3 && rows[0].Contains("07:00 +01:00 [surrounding context]") && rows[1].Contains("10:00 +01:00:")
            && rows[2].Contains("13:00 +01:00 [surrounding context]"), "Individual three-hour entries, exclusive end and one context entry each side");
        Check(rows[1].Contains("Humidity: 13%") && rows[1].Contains("mph") && rows[1].Contains("30%") && rows[1].Contains("mm/3h")
            && !text.Contains("Temperature:") && !text.Contains(" Day:"), "Explicit fields, units, accumulation semantics and no averaging");
        text = await Call("get_weather", new { when = "2026-10-29T11:00/2026-10-29T12:00", details = "Humidity" });
        Check(text.Contains("no forecast timestamp inside") && text.Split('\n').Count(line => line.StartsWith("2026-")) == 2, "Narrow gap returns surrounding data, never invented exact-hour values");
        var explicitUtc = await Call("get_weather", new { when = "2026-10-29T10:00Z/2026-10-29T11:00Z", details = "Humidity" });
        Check(explicitUtc.Contains("Requested window: 2026-10-29 11:00 +01:00 to 2026-10-29 12:00 +01:00"), "UTC input converted to destination time");
        text = await Call("get_air_pollution", new { when = "2026-10-29T10:00/2026-10-29T12:00", details = "NH3" });
        rows = text.Split('\n').Where(line => line.StartsWith("2026-")).ToArray();
        Check(rows.Length == 4 && rows[0].Contains("09:00 +01:00 [surrounding context]") && rows[^1].Contains("12:00 +01:00 [surrounding context]")
            && text.Contains("Resolution: 1-hour steps") && text.Contains("NH₃:") && !text.Contains("AQI"), "Pollution hourly detail override");
        text = await Call("get_weather", new { when = "2026-10-28T00:00/2026-10-29T04:00", details = "Humidity" });
        Check(text.Contains("bounds extend beyond") && text.Contains("only the listed times"), "Partial coverage is explicit");
        text = await Call("get_weather", new { when = "2026-10-29T00:00/2026-10-30T00:00", details = "Temp" });
        Check(text.Contains("Requested window: 2026-10-29 00:00 +01:00 to 2026-10-30 00:00 +01:00"), "Date-only endpoints are destination midnight");
        foreach (var op in new[] { "get_weather", "get_air_pollution" })
        {
            var count = transport.Requests;
            foreach (var arguments in new object[] { new { when = "next 0 hours" }, new { when = "some time later" },
                new { when = "2026-02-30T00:00/2026-03-01T00:00" }, new { when = "tomorrowT25:00/tomorrowT26:00" },
                new { when = "next weekT00:00/laterT00:00" }, new { when = 12 } })
                Check((await catalog.CallToolAsync(op, JsonSerializer.SerializeToElement(arguments), default)).IsError, "Invalid forecast request");
            Check(transport.Requests == count, "Malformed range is rejected before network requests");
            foreach (var arguments in new[] {
                new { when = "2030-01-01T00:00/2030-01-02T00:00" }, new { when = "2020-01-01T00:00/2020-01-02T00:00" },
                new { when = "2026-10-29T12:00/2026-10-29T10:00" } })
            {
                var result = await catalog.CallToolAsync(op, JsonSerializer.SerializeToElement(arguments), default);
                Check(result.IsError && !result.Text.Contains("Humidity:"), "Out-of-range and reversed windows never fall back to broad data");
            }
        }
        text = await Call("get_weather", new { when = "forecast" });
        Check(text.Contains(" Day:") && !text.Contains("Requested window:") && !text.Contains("Humidity:"), "Omitted range retains default weather summary");
        text = await Call("get_air_pollution", new { when = "forecast" });
        Check(text.Contains("UTC day/night averages") && text.Contains("AQI") && !text.Contains("NH₃:"), "Omitted range retains default pollution summary");

        // A Monday request must not turn a future weekend into Monday-Friday data.
        clock.Now = DateTimeOffset.Parse("2026-10-26T00:00Z", CultureInfo.InvariantCulture);
        transport.First = clock.Now;
        transport.WeatherCount = 40; // Five days of 3-hour samples, ending Friday evening.
        transport.PollutionCount = 96; // Four days of hourly samples.
        foreach (var op in new[] { "get_weather", "get_air_pollution" })
        {
            var calls = transport.Requests;
            var noWeekend = await catalog.CallToolAsync(op, JsonSerializer.SerializeToElement(new { when = "this weekend" }), default);
            Check(noWeekend.IsError && noWeekend.Text.Contains("2026-10-31") && noWeekend.Text.Contains("2026-11-01")
                && noWeekend.Text.Contains("Available forecast timestamps:") && noWeekend.Text.Contains("No forecast coverage")
                && noWeekend.Text.Contains("Do not extrapolate") && !noWeekend.Text.Contains("Temperature:"), "Monday weekend beyond horizon returns dates/limits, never unrelated weather");
            Check(transport.Requests == calls + 2, "No retry or alternate forecast request for unavailable weekend");
            var nextWeekend = await catalog.CallToolAsync(op, JsonSerializer.SerializeToElement(new { when = "next weekend" }), default);
            Check(nextWeekend.IsError && nextWeekend.Text.Contains("2026-11-07") && nextWeekend.Text.Contains("2026-11-08"), "Next weekend is not moved into available dates");
            text = await Call(op, new { when = "this week" });
            Check(text.Contains("Partial forecast coverage") && text.Contains("No forecast coverage for this period") && text.Contains("2026-11-01"), "Partly available week identifies missing requested dates");
            text = await Call(op, new { when = "next 3 afternoons" });
            Check(text.Split('\n').Count(line => line.StartsWith("Requested window:")) == 3
                && text.Contains("Periods are separate"), "Multi-day periods stay separate in real tool output");
            foreach (var line in text.Split('\n').Where(line => line.StartsWith("2026-") && !line.Contains("[surrounding context]")))
            {
                var hour = int.Parse(line.Substring(11, 2), CultureInfo.InvariantCulture);
                Check(hour >= 12 && hour < 18, "Intervening nights are never inside selected afternoons");
            }
            text = await Call(op, new { when = "next 5 hours" });
            Check(text.Contains("2026-10-26 01:00 +01:00 to 2026-10-26 06:00 +01:00"), "Next five hours start at now");
            text = await Call(op, new { when = "today" });
            Check(text.Contains("2026-10-26 01:00 +01:00 to 2026-10-27 00:00 +01:00"), "Today means remaining local day");
        }
        // Saturday alone is available: Sunday must be explicitly unavailable, not averaged into Saturday.
        transport.First = DateTimeOffset.Parse("2026-10-30T00:00Z", CultureInfo.InvariantCulture);
        transport.WeatherCount = 16;
        text = await Call("get_weather", new { when = "this weekend" });
        Check(text.Contains("Partial forecast coverage") && text.Contains("2026-11-01 00:00")
            && text.Contains("No forecast coverage for this period"), "Partial weekend preserves the missing Sunday");
        transport.First = DateTimeOffset.Parse("2026-10-26T12:00Z", CultureInfo.InvariantCulture);
        transport.WeatherCount = 40;
        text = await Call("get_weather", new { when = "forecast" });
        Check(text.Contains("Available forecast timestamps:") && text.Contains("2026-10-31 Saturday"),
            "General forecast includes the sixth calendar date touched by five elapsed days and reports actual validity times");
        // Runtime validation also rejects malformed JSON counts and incompatible selections before networking.
        foreach (var op in new[] { "get_weather", "get_air_pollution" })
        {
            var calls = transport.Requests;
            foreach (var json in new[] { "{\"range\":\"days\"}", "{\"count\":2}", "{\"range\":\"hours\",\"count\":0}",
                "{\"range\":\"days\",\"count\":1.5}", "{\"range\":\"days\",\"count\":\"3\"}",
                "{\"range\":\"days\",\"count\":true}", "{\"range\":\"days\",\"count\":32}",
                "{\"date\":\"today\",\"range\":\"weekend\"}", "{\"period\":\"10:00-10:00\"}",
                "{\"start\":\"today\",\"end\":\"tomorrow\",\"range\":\"days\",\"count\":2}" })
                Check((await catalog.CallToolAsync(op, JsonSerializer.Deserialize<JsonElement>(json), default)).IsError, "Invalid selector: " + json);
            Check(calls == transport.Requests, "Invalid combinations fail before provider calls");
        }

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
        var civilDay = ForecastSelectionRequest.Parse(null, null, "2026-10-25")!.Resolve(geo, now.AddDays(-5)).Windows.Single();
        var night = ForecastSelectionRequest.Parse(null, null, "2026-10-24", "night")!.Resolve(geo, now.AddDays(-5)).Windows.Single();
        Check((civilDay.End - civilDay.Start).TotalHours == 25 && (night.End - night.Start).TotalHours == 9, "Date/period advances civil date across DST");
        foreach (var (period, startHour, endHour) in new[] { ("day", 0, 0), ("morning", 6, 12), ("afternoon", 12, 18), ("evening", 18, 22), ("night", 22, 6) })
        {
            var window = ForecastSelectionRequest.Parse(null, null, "tomorrow", period)!.Resolve(geo, now).Windows.Single();
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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Transport : HttpMessageHandler
    {
        public int Requests;
        public DateTimeOffset First = DateTimeOffset.Parse("2026-10-29T00:00Z", CultureInfo.InvariantCulture);
        public int WeatherCount = 9, PollutionCount = 25;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            var first = First;
            var geo = new GeoResult { Name = "Berlin", Country = "DE", Lat = 52.52, Lon = 13.405 };
            object data;
            if (request.RequestUri!.AbsolutePath.Contains("/geo/")) data = new[] { geo };
            else if (request.RequestUri.AbsolutePath.Contains("air_pollution"))
                data = new OpenWeatherAirPollutionResponse { List = Enumerable.Range(0, PollutionCount).Select(i => new AirPollutionData
                { Dt = first.AddHours(i).ToUnixTimeSeconds(), Main = new() { Aqi = 2 }, Components = new() { Nh3 = i } }).ToList() };
            else data = new OpenWeatherForecastResponse
            {
                City = new() { Coord = new() { Lat = 52.52, Lon = 13.405 }, Country = "DE", Timezone = 0 }, // Deliberately not Berlin's offset.
                List = Enumerable.Range(0, WeatherCount).Select(i => new ForecastItem
                {
                    Dt = first.AddHours(i * 3).ToUnixTimeSeconds(), Main = new() { Temp = 50 + i, FeelsLike = 49 + i, TempMin = 48 + i, TempMax = 52 + i, Humidity = 10 + i },
                    Weather = [new() { Description = "clear", Icon = "01d" }], Wind = new() { Speed = 2 + i, Gust = 3 + i },
                    Clouds = new() { All = 10 }, Sys = new(), Pop = (i % 10) / 10.0, Rain = new() { ThreeHours = i },
                }).ToList(),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(data), System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
