using System.Globalization;
using System.Text.Json;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.VisionCapture;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.OpenWeather.ChatAugmentations;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.OpenWeather.Helper;
using Voxta.Modules.Aios.Tools;
using MapTargetType = Voxta.Modules.Aios.OpenWeather.ChatAugmentations.OpenWeatherChatAugmentationsServiceInstance.MapTargetType;

namespace Voxta.Modules.Aios.OpenWeather.Tools;

internal sealed class OpenWeatherToolHandler(
    IOpenWeatherClient client, OpenWeatherChatAugmentationsSettings settings, CultureInfo culture,
    CancellationToken sessionAbort = default)
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
            if (result.Value is not { Length: > 0 }) return new("OpenWeather returned an empty map image.", IsError: true);
            return new($"Weather map for {target.Identifier}: {WeatherMapHelper.ToDisplayName(normalizedLayer)}.")
            {
                Images = [new BytesImage("image/png", result.Value, ComputerVisionSource.Screen)
                { FileName = $"weathermap_{normalizedLayer}_{target.Identifier}.png" }],
            };
        }

        location = string.IsNullOrWhiteSpace(location) ? settings.MyLocation?.Trim() : location;
        if (string.IsNullOrWhiteSpace(location))
            throw new ModuleToolInputException("No location was supplied and My Location is not configured. Ask the user which city to use, then call the tool with location.");

        switch (operation)
        {
            case "get_weather":
            {
                var result = await client.FetchWeatherData(location, settings.Units, token);
                token.ThrowIfCancellationRequested();
                return result.Success ? new(WeatherReportFormatter.Weather(result.Value!, location, settings.Units, _weatherDetails)) : Failure(result);
            }
            case "get_weather_forecast":
            {
                var result = await client.FetchForecastData(location, settings.Units, token);
                token.ThrowIfCancellationRequested();
                if (!result.Success) return Failure(result);
                var data = result.Value!;
                if (data.List.Count == 0) return new("OpenWeather returned no weather forecast data.", IsError: true);
                var summary = WeatherForecastSummariser.Summarise(data.List, culture, _weatherDetails,
                    days: 5, unitSuffix: settings.Units == "imperial" ? "°F" : "°C", windUnit: WeatherReportFormatter.WindUnit(settings.Units));
                return new($"Weather forecast for {location} ({data.City.Country}):\n{summary}");
            }
            case "get_air_pollution":
            {
                var result = await client.FetchAirPollutionData(location, token);
                token.ThrowIfCancellationRequested();
                if (!result.Success) return Failure(result);
                if (result.Value!.List.Count == 0) return new("OpenWeather returned no air quality data.", IsError: true);
                return new(WeatherReportFormatter.Pollution(result.Value, location, _pollutionDetails));
            }
            case "get_air_pollution_forecast":
            {
                var result = await client.FetchAirPollutionForecastData(location, token);
                token.ThrowIfCancellationRequested();
                if (!result.Success) return Failure(result);
                if (result.Value!.List.Count == 0) return new("OpenWeather returned no air quality forecast data.", IsError: true);
                return new($"Air pollution forecast for {location}:\n{AirPollutionForecastSummariser.Summarise(result.Value.List, culture, _pollutionDetails, days: 5)}");
            }
            default: throw new ModuleToolInputException("Unknown OpenWeather operation.");
        }
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
