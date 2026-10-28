using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Services.Agents;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.OpenWeather.Tools;

internal static class OpenWeatherToolCatalog
{
    private sealed record Definition(string Name, string Description, FunctionArgumentDefinition[] Arguments);
    private static FunctionArgumentDefinition Location() => new()
    {
        Name = "location", Type = FunctionArgumentType.String, Required = false,
        Description = "City and optional state/country, preserving full names; postal code as zip:10115,DE (country required); or latitude,longitude using decimal points, e.g. 52.52,13.405. Omit to use My Location from settings. If no location is configured, ask the user. Do not guess a location.",
    };
    private static FunctionArgumentDefinition Details(string fields) => new()
    {
        Name = "details", Type = FunctionArgumentType.String, Required = false,
        Description = "Comma-separated fields: " + fields + ". Omit for configured defaults on general requests. For explicit questions, request the relevant fields even if unchecked in settings; this selection replaces defaults for this call.",
    };
    private static FunctionArgumentDefinition Date(string name, string description) => new()
    {
        Name = name, Type = FunctionArgumentType.String, Required = true,
        Description = description + " ISO 8601 date-time with Z or explicit UTC offset, e.g. 2026-10-27T00:00:00+01:00. Ask for clarification if the intended date/time zone is unknown.",
    };
    private static FunctionArgumentDefinition ForecastTime(string name, string horizon) => new()
    {
        Name = name, Type = FunctionArgumentType.String, Required = false,
        Description = "Supply BOTH start and end only when the user asks for a specific time frame; otherwise omit both for day/night summaries. " +
            (name == "start" ? "Inclusive start. " : "Exclusive end, later than start. ") +
            "Use today/tomorrow, YYYY-MM-DD, or a date with THH:mm[:ss] and optional Z or UTC offset. Without an offset, times use the destination's time zone, including daylight saving; date-only values mean local midnight. " +
            "Example morning: start=tomorrowT06:00, end=tomorrowT12:00. Single hour: 14:00 through 15:00 on the requested date. " + horizon +
            " Actual returned timestamps determine availability. Targeted results include individual entries and one surrounding entry per side, marked as context; never interpolate or extrapolate. For repeated/skipped DST clock times, ask for an explicit offset.",
    };
    private static readonly Definition[] Definitions =
    [
        new("get_weather", "Get current weather for a requested city or the configured My Location, using configured units. Use details for explicitly requested fields. Returned sunrise/sunset is ONLY for the supplied date; use get_sun_times for tomorrow or another date. Never extrapolate today's sun times.", [Location(), Details(WeatherDetailSelection.Weather)]),
        new("get_weather_forecast", "Get weather forecasts up to approximately five days ahead in three-hour steps. Omit start/end for day/night summaries; specify both for a targeted time frame with individual entries and surrounding context. Use the requested city or configured My Location and configured units. Use details for explicit questions. This endpoint cannot supply future sunrise/sunset: use get_sun_times with the requested date.", [Location(), Details(WeatherDetailSelection.Forecast), ForecastTime("start", "Weather: approximately five days ahead, three-hour steps. "), ForecastTime("end", "Weather: approximately five days ahead, three-hour steps. ")]),
        new("get_sun_times", "Get calculated sunrise and sunset for a specific local date, including tomorrow, at a requested location or My Location. Uses coordinates and destination time-zone rules (including daylight saving). These are astronomical estimates, not weather observations. Use this for date-specific sun questions; do not extrapolate today's weather metadata.",
        [
            Location(),
            new() { Name = "date", Type = FunctionArgumentType.String, Required = true, Description = "Requested date at the destination: today, tomorrow, or YYYY-MM-DD (1901-2099). Pass tomorrow directly for tomorrow's sunrise/sunset; the tool resolves the destination's current date itself." },
        ]),
        new("get_air_pollution", "Get current air quality and selected pollutant concentrations for a requested city or configured My Location. AQI uses OpenWeather's 1 (good) to 5 (very poor) scale. Use details for explicit pollutant questions even if unchecked in settings.", [Location(), Details(WeatherDetailSelection.Pollution)]),
        new("get_air_pollution_forecast", "Get air quality forecasts up to approximately four days ahead in hourly steps, using default or explicitly requested pollutant details. Omit start/end for day/night summaries; specify both for individual entries in a targeted time frame with surrounding context. Use the requested city or configured My Location. AQI uses OpenWeather's 1 to 5 scale. Use details for explicit pollutant questions even if unchecked in settings.", [Location(), Details(WeatherDetailSelection.Pollution), ForecastTime("start", "Pollution: approximately four days ahead, hourly steps. "), ForecastTime("end", "Pollution: approximately four days ahead, hourly steps. ")]),
        new("get_air_pollution_history", "Get historical air quality and pollutants from 27 November 2020 onward. Request up to 31 days per call; results summarize UTC day/night periods. AQI uses OpenWeather's 1 to 5 scale. Use details for explicit pollutant questions.",
            [Location(), Date("start", "Inclusive start."), Date("end", "Exclusive end; later than start and no later than now."), Details(WeatherDetailSelection.Pollution)]),
        new("get_weather_map", "Get a weather map image for the world, a continent or a country. Omit location for a global map. City maps are unsupported: ask for a country or continent instead. The result includes the image, geographic bounds, layer units and a color reference. When vision is available, briefly describe the visible regional pattern using the supplied legend; do not invent a summary if you cannot inspect the image.",
        [
            new() { Name = "layer", Description = "Requested weather layer: clouds, precipitation, pressure, wind or temp.", Type = FunctionArgumentType.String, Required = true },
            new() { Name = "location", Description = "Country name or ISO alpha-2 code, continent name, or global. Omit for global. This does not use My Location.", Type = FunctionArgumentType.String, Required = false },
        ]),
    ];

    public static readonly ChatToolChoice[] Choices = Definitions.Select(d => new ChatToolChoice(d.Name, d.Description, true)).ToArray();

    public static string DescribeArguments(string name) =>
        ModuleToolDocumentation.Arguments(Definitions[Array.FindIndex(Choices, choice => choice.Name == name)].Arguments);

    public static ModuleToolCatalog Create(OpenWeatherToolHandler handler, OpenWeatherToolsSettings settings, ILogger logger) => new(
        Definitions.Select((d, i) => new ModuleTool(new ChatTool(Choices[i].Name,
            settings.Descriptions?.GetValueOrDefault(Choices[i].Name) ?? d.Description,
            ChatToolSchema.FromArguments(d.Arguments), VoxtaModule.ServiceName)
        {
            AugmentationKey = VoxtaModule.ToolsAugmentationKey,
            Enabled = settings.Selected.Contains(Choices[i].Name),
            ChatStyles = settings.ChatStyles,
            Permission = ToolPermissionClass.Read,
            ExternalContent = true,
        }, (args, token) => handler.ExecuteAsync(d.Name, args, token))), logger);
}
