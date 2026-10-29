using System.Globalization;
using Voxta.Abstractions.Registration;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.OpenWeather.ChatAugmentations;
using Voxta.Modules.Aios.OpenWeather.Tools;
using Voxta.Modules.Aios.Tools;

internal static class WeatherDataChecks
{
    public static async Task RunAsync()
    {
        using var transport = new Transport();
        using var http = new HttpClient(transport);
        var client = new OpenWeatherClient(http, "test-key", NullLogger<OpenWeatherClient>.Instance);
        var settings = new OpenWeatherChatAugmentationsSettings
        { MyLocation = "Berlin, DE", Units = "metric", WeatherDetails = ["Temp"], PollutionDetails = ["AQI"], TileCachePath = "unused" };
        var catalog = OpenWeatherToolCatalog.Create(new(client, settings, CultureInfo.InvariantCulture, (_, _, _) => Task.CompletedTask),
            OpenWeatherToolsSettings.From(new StaticSettingsSource(new())), NullLogger.Instance);
        async Task<string> Call(string tool, object args)
        {
            var result = await catalog.CallToolAsync(tool, JsonSerializer.SerializeToElement(args), default);
            Check(!result.IsError, result.Text);
            return result.Text;
        }
        foreach (var (tool, fields) in new[] { ("get_weather", WeatherDetailSelection.Weather), ("get_weather_forecast", WeatherDetailSelection.Forecast) })
        {
            foreach (var field in fields.Split(','))
            {
                var output = await Call(tool, new { details = field });
                var expected = field switch
                {
                    "Temp" => tool == "get_weather" ? "current temperature" : "Temperature:",
                    "FeelsLike" => tool == "get_weather" ? "feels like" : "Feels like:",
                    "Condition" => "clear", "Precipitation" => "Rain:",
                    "TempMinMax" => tool == "get_weather" ? "temperature ranges" : "Sample temperature bounds",
                    "Wind" => "Wind:", "WindGust" => "wind gust:", "CloudCover" => "Cloud cover:",
                    "Visibility" => "Visibility:", "Humidity" => "Humidity:", "Pressure" => "Pressure:",
                    "SeaLevelPressure" => "Sea-level pressure:", "GroundPressure" => "Ground-level pressure:",
                    "Sunrise" => tool == "get_weather" ? "Sunrise:" : "get_sun_times",
                    "Sunset" => tool == "get_weather" ? "Sunset:" : "get_sun_times",
                    "ObservationTime" => tool == "get_weather" ? "Data time:" : "Forecast valid times",
                    "PrecipitationProbability" => "precipitation probability:",
                    _ => throw new Exception("Missing verification for " + field),
                };
                Check(output.Contains(expected, StringComparison.OrdinalIgnoreCase), "Selected detail rendered: " + tool + "." + field + ": " + output);
            }
        }
        foreach (var tool in new[] { "get_air_pollution", "get_air_pollution_forecast", "get_air_pollution_history" })
            foreach (var field in WeatherDetailSelection.Pollution.Split(','))
            {
                var output = await Call(tool, new { details = field, start = tool.EndsWith("history") ? "2020-11-28T00:00:00Z" : (string?)null, end = tool.EndsWith("history") ? "2020-11-29T00:00:00Z" : null });
                Check(output.Contains(field == "AQI" ? "AQI" : "\u00b5g/m\u00b3"), "Selected pollutant rendered: " + tool + "." + field);
            }
        var combined = await Call("get_weather_forecast", new { details = "Temp,TempMinMax" });
        Check(combined.Contains("Temperature:") && combined.Contains("Sample temperature bounds"), "Selecting bounds does not hide requested average temperature");
        var report = await Call("get_weather", new { details = "humidity,Sunrise,Sunset,Pressure,WindGust,ObservationTime" });
        Check(report.Contains("Humidity: 64%") && report.Contains("Pressure: 1015 hPa") && report.Contains("Wind gust: 7 m/s"), "Explicit weather fields");
        Check(report.Contains("2020-11-28 08:00 +02:00") && report.Contains("2020-11-28 18:00 +02:00"), "Sun times use destination offset");
        Check(!report.Contains("current temperature"), "Override replaces defaults");
        report = await Call("get_weather", new { });
        Check(report.Contains("current temperature") && !report.Contains("Humidity:"), "Override does not mutate defaults");
        report = await Call("get_air_pollution", new { details = "NH3" });
        Check((report.Contains("NH₃: 3.5") || report.Contains("NH₃: 3,5")) && !report.Contains("AQI"), "Unchecked NH3 available on request: " + report);
        report = await Call("get_air_pollution_forecast", new { details = "NH3" });
        Check((report.Contains("NH₃: 3.5") || report.Contains("NH₃: 3,5")) && !report.Contains("AQI"), "Forecast override");
        report = await Call("get_weather_forecast", new { details = "Humidity,PrecipitationProbability,Sunrise" });
        Check(report.Contains("Humidity: 64%") && report.Contains("80%") && report.Contains("Use get_sun_times") && !report.Contains("Rain:"), "Forecast fields respect selection");
        report = await Call("get_air_pollution_history", new { start = "2020-11-28T00:00:00Z", end = "2020-11-29T00:00:00Z", details = "NH3" });
        Check(report.Contains("2020-11-28") && (report.Contains("NH₃: 3.5") || report.Contains("NH₃: 3,5")) && transport.Urls.Any(u => u.AbsolutePath.EndsWith("/history") && u.Query.Contains("start=1606521600") && u.Query.Contains("end=1606608000")), "History range, dates and override");
        var count = transport.Urls.Count;
        foreach (var args in new object[] {
            new { start = "2020-11-26T00:00:00Z", end = "2020-11-29T00:00:00Z" },
            new { start = "2020-11-28", end = "2020-11-29" },
            new { start = "2020-11-28T00:00:00Z", end = "2021-01-01T00:00:00Z" },
            new { start = "2020-11-29T00:00:00Z", end = "2020-11-28T00:00:00Z" },
            new { start = "2099-11-28T00:00:00Z", end = "2099-11-29T00:00:00Z" } })
            Check((await catalog.CallToolAsync("get_air_pollution_history", JsonSerializer.SerializeToElement(args), default)).IsError, "Invalid history range");
        Check((await catalog.CallToolAsync("get_weather", JsonSerializer.SerializeToElement(new { details = "NH3" }), default)).IsError && transport.Urls.Count == count, "Invalid inputs do not call provider");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            await Call("get_weather", new { location = "Frankfurt am Main, Germany" });
            Check(Uri.UnescapeDataString(transport.Urls[^2].Query).Contains("q=Frankfurt am Main,DE"), "Multiword city preserved");
            await Call("get_weather", new { location = "New York, NY, US" });
            Check(Uri.UnescapeDataString(transport.Urls[^2].Query).Contains("q=New York,NY,US"), "State preserved: " + Uri.UnescapeDataString(transport.Urls[^2].Query));
            await Call("get_weather", new { location = "zip:SW1A 1AA,GB" });
            Check(transport.Urls[^2].AbsolutePath.EndsWith("/zip") && Uri.UnescapeDataString(transport.Urls[^2].Query).Contains("SW1A 1AA,GB"), "Alphanumeric postal code");
            await Call("get_weather", new { location = "52.52,13.405" });
            Check(transport.Urls[^2].AbsolutePath.EndsWith("/reverse") && transport.Urls[^1].Query.Contains("lat=52.52&lon=13.405"), "Reverse lookup retains exact coordinates and invariant decimals");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        count = transport.Urls.Count;
        Check(!(await client.ResolveLocationAsync("91,0", default)).Success && !(await client.ResolveLocationAsync("zip:10115", default)).Success && transport.Urls.Count == count, "Invalid coordinates and missing postal country");
        transport.Status = HttpStatusCode.TooManyRequests;
        Check((await client.FetchAirPollutionHistoryData("Berlin", 1606521600, 1606608000, default)).State == OpenWeatherOperationState.Unavailable, "History geocoding rate limit");
        transport.Status = HttpStatusCode.OK;
        transport.EmptyHistory = true;
        Check((await catalog.CallToolAsync("get_air_pollution_history", JsonSerializer.SerializeToElement(new { start = "2020-11-28T00:00:00Z", end = "2020-11-29T00:00:00Z" }), default)).IsError, "Empty history");
        Check(transport.Urls.All(u => u.Scheme == "https"), "Requests use HTTPS");
        Console.WriteLine("PASS detail overrides, defaults, sun times, history validation, postal/reverse geocoding and invariant coordinates");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Transport : HttpMessageHandler
    {
        public List<Uri> Urls = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool EmptyHistory;
        private const string Geo = """{"name":"Berlin","country":"DE","lat":52,"lon":13}""";
        private const string Weather = """{"dt":1606564800,"timezone":7200,"main":{"temp":12,"feels_like":10,"temp_min":11,"temp_max":13,"humidity":64,"pressure":1015},"weather":[{"description":"clear","icon":"01d"}],"wind":{"speed":2,"deg":90,"gust":7},"clouds":{"all":0},"sys":{"country":"DE","sunrise":1606543200,"sunset":1606579200}}""";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Urls.Add(uri);
            var path = uri.AbsolutePath;
            var json = path.EndsWith("/zip") ? Geo : path.Contains("/geo/") ? "[" + Geo + "]"
                : path.Contains("air_pollution") ? (EmptyHistory && path.EndsWith("/history") ? "{\"list\":[]}" : """{"list":[{"dt":1606564800,"main":{"aqi":2},"components":{"nh3":3.5}}]}""")
                : path.EndsWith("/forecast") ? "{\"city\":{\"country\":\"DE\",\"coord\":{},\"timezone\":7200,\"sunrise\":1606543200},\"list\":[" + Weather[..^1] + ",\"pop\":0.8}]}"
                : Weather;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
