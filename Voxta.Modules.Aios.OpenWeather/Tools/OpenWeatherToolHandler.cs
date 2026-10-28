using System.Globalization;
using System.Text.Json;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.VisionCapture;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.OpenWeather.ChatAugmentations;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.OpenWeather.Helper;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.OpenWeather.Tools;

internal sealed class OpenWeatherToolHandler(
    IOpenWeatherClient client, OpenWeatherChatAugmentationsSettings settings, CultureInfo culture,
    Func<string, IImage, CancellationToken, Task> attachImage, CancellationToken sessionAbort = default)
{
    private readonly HashSet<string> _weatherDetails = (settings.WeatherDetails ?? ["Temp"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pollutionDetails = (settings.PollutionDetails ?? ["AQI"]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<ChatToolCallResult> ExecuteAsync(string operation, JsonElement? arguments, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionAbort);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        var location = Text(arguments, "location");
        if (operation == "get_weather_map")
        {
            var layer = Text(arguments, "layer");
            if (!WeatherMapHelper.TryNormalizeLayer(layer, out var normalizedLayer))
                throw new ModuleToolInputException("Choose a map layer: clouds, precipitation, pressure, wind or temp.");
            var target = MapTarget(location);
            var result = await client.FetchWeatherMapAsync(target, normalizedLayer, settings.TileCachePath, token);
            token.ThrowIfCancellationRequested();
            if (!result.Success) return Failure(result);
            if (result.Value is not { Bytes.Length: > 0 }) return new("OpenWeather returned an empty map image.", IsError: true);
            var description = $"Weather map for {target.Identifier}: {WeatherMapHelper.ToDisplayName(normalizedLayer)}.";
            var image = new BytesImage("image/png", result.Value.Bytes, ComputerVisionSource.Tool)
            { FileName = $"weathermap_{normalizedLayer}_{target.Identifier}.png" };
            // SDK result images reach the model. A chat attachment also makes the map visible to the user,
            // matching Voxta's own MCP image-tool integration. This does not trigger another reply.
            await attachImage(description, image, token);
            token.ThrowIfCancellationRequested();
            return new(WeatherMapPresentation.Describe(result.Value, target, normalizedLayer)) { Images = [image] };

        }

        location = string.IsNullOrWhiteSpace(location) ? settings.MyLocation?.Trim() : location;
        if (string.IsNullOrWhiteSpace(location))
            throw new ModuleToolInputException("No location was supplied and My Location is not configured. Ask the user which city to use, then call the tool with location.");

        var pollution = operation.StartsWith("get_air_pollution", StringComparison.Ordinal);
        var details = WeatherDetailSelection.Resolve(Text(arguments, "details"), pollution ? _pollutionDetails : _weatherDetails,
            pollution ? WeatherDetailSelection.Pollution : operation == "get_weather_forecast" ? WeatherDetailSelection.Forecast : WeatherDetailSelection.Weather);

        switch (operation)
        {
            case "get_weather":
            {
                var result = await client.FetchWeatherData(location, settings.Units, token);
                token.ThrowIfCancellationRequested();
                return result.Success ? new(WeatherReportFormatter.Weather(result.Value!, location, settings.Units, details)) : Failure(result);
            }
            case "get_weather_forecast":
            {
                var result = await client.FetchForecastData(location, settings.Units, token);
                token.ThrowIfCancellationRequested();
                if (!result.Success) return Failure(result);
                var data = result.Value!;
                if (data.List.Count == 0) return new("OpenWeather returned no weather forecast data.", IsError: true);
                var summary = WeatherForecastSummariser.Summarise(data.List, culture, details,
                    days: 5, unitSuffix: settings.Units == "imperial" ? "°F" : "°C", windUnit: WeatherReportFormatter.WindUnit(settings.Units), utcOffset: data.City.Timezone ?? 0);
                if (details.Contains("Sunrise")) summary += $"\nCity sunrise: {WeatherReportFormatter.LocalTime(data.City.Sunrise, data.City.Timezone)} (single supplied date, not daily predictions).";
                if (details.Contains("Sunset")) summary += $"\nCity sunset: {WeatherReportFormatter.LocalTime(data.City.Sunset, data.City.Timezone)} (single supplied date, not daily predictions).";
                return new($"Weather forecast for {location} ({data.City.Country}):\n{summary}");
            }
            case "get_air_pollution":
            {
                var result = await client.FetchAirPollutionData(location, token);
                token.ThrowIfCancellationRequested();
                if (!result.Success) return Failure(result);
                if (result.Value!.List.Count == 0) return new("OpenWeather returned no air quality data.", IsError: true);
                return new(WeatherReportFormatter.Pollution(result.Value, location, details));
            }
            case "get_air_pollution_forecast":
            {
                var result = await client.FetchAirPollutionForecastData(location, token);
                token.ThrowIfCancellationRequested();
                if (!result.Success) return Failure(result);
                if (result.Value!.List.Count == 0) return new("OpenWeather returned no air quality forecast data.", IsError: true);
                return new($"Air pollution forecast for {location} (UTC day/night averages):\n{AirPollutionForecastSummariser.Summarise(result.Value.List, culture, details, days: 5)}");
            }
            case "get_air_pollution_history":
            {
                var start = HistoryDate(arguments, "start");
                var end = HistoryDate(arguments, "end");
                if (start < new DateTimeOffset(2020, 11, 27, 0, 0, 0, TimeSpan.Zero) || end <= start || end > DateTimeOffset.UtcNow || end - start > TimeSpan.FromDays(31))
                    throw new ModuleToolInputException("History requires start >= 2020-11-27 UTC, end after start and no later than now, and at most 31 days per call.");
                var result = await client.FetchAirPollutionHistoryData(location, start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds(), token);
                token.ThrowIfCancellationRequested();
                if (!result.Success) return Failure(result);
                var items = result.Value!.List.Where(x => x.Dt >= start.ToUnixTimeSeconds() && x.Dt < end.ToUnixTimeSeconds()).ToList();
                if (items.Count == 0) return new("OpenWeather returned no air quality history in the requested range.", IsError: true);
                return new($"Air pollution history for {location}, {start:O} to {end:O} (end exclusive). UTC day/night averages:\n{AirPollutionForecastSummariser.Summarise(items, culture, details, days: 32)}");
            }
            default: throw new ModuleToolInputException("Unknown OpenWeather operation.");
        }
    }

    private static DateTimeOffset HistoryDate(JsonElement? arguments, string name)
    {
        var text = Text(arguments, name);
        if (text == null || !System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?(?:Z|[+-]\d{2}:\d{2})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new ModuleToolInputException(name + " must be an ISO 8601 date-time with Z or an explicit UTC offset.");
        return date;
    }

    private static ChatToolCallResult Failure<T>(OpenWeatherResult<T> result) => new(
        string.IsNullOrWhiteSpace(result.UserVisibleError) ? "OpenWeather could not complete the request." : result.UserVisibleError, IsError: true);

    private static string? Text(JsonElement? arguments, string name)
    {
        if (arguments is not { } args || !args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ModuleToolInputException($"{name} must be a string.");
        return value.GetString()?.Trim();
    }

    private static (MapTargetType Type, string Identifier) MapTarget(string? location)
    {
        if (string.IsNullOrWhiteSpace(location) || new[] { "global", "world", "earth" }.Contains(location, StringComparer.OrdinalIgnoreCase))
            return (MapTargetType.Global, "Global");
        if (WeatherMapHelper.ContinentMap.TryGetValue(location.Replace(" ", ""), out var continent))
            return (MapTargetType.Continent, continent);
        if (CountryCodeMap.TryGetAlpha2(location, out var country)) return (MapTargetType.Country, country!);
        throw new ModuleToolInputException("Map location must be a country, continent or global. Ask the user to clarify; city maps are unsupported.");
    }
}
