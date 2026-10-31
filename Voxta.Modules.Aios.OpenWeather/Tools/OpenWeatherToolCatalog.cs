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
        Description = "City, zip:postal-code,country, or latitude,longitude. Omit for My Location.",
    };
    private static FunctionArgumentDefinition Details(string fields) => new()
    {
        Name = "details", Type = FunctionArgumentType.String, Required = false,
        Description = "Comma-separated fields: " + fields + ". Omit for configured defaults; an explicit selection replaces them. Names are case-insensitive.",
    };
    private static FunctionArgumentDefinition When(string horizon) => new()
    {
        Name = "when", Type = FunctionArgumentType.String, Required = false,
        Description = "Omit or now for current conditions. Forecast: today, tomorrow, day after tomorrow, weekday/next weekday, this/next weekend or week, next N hours/days, N days from tomorrow, YYYY-MM-DD, or YYYY-MM-DD to YYYY-MM-DD (inclusive dates). Add morning, afternoon, evening, night or HH:mm-HH:mm; next N afternoons also works. Examples: Saturday morning; next 5 hours; tomorrow 10:00-18:00. Use forecast for all available dates; ISO-start/ISO-end for an exact interval (exclusive end). " + horizon,
    };
    private static FunctionArgumentDefinition HistoryDate(string name, string boundary) => new()
    {
        Name = name, Type = FunctionArgumentType.String, Required = true,
        Description = boundary + " ISO date-time with Z or UTC offset, e.g. 2026-10-01T00:00:00Z.",
    };
    private static readonly Definition[] Definitions =
    [
        new("get_weather", "Get current weather, or a forecast when a period is supplied. Forecasts cover approximately five days in three-hour steps; results identify unavailable periods. Use get_sun_times for date-specific sunrise/sunset.",
            [Location(), When("Forecast availability: approximately five days; actual returned dates apply."), Details(WeatherDetailSelection.Forecast)]),
        new("get_air_pollution", "Get current air quality, or an hourly forecast when a period is supplied (approximately four days). Results identify unavailable periods. AQI uses OpenWeather's 1 (good) to 5 (very poor) scale.",
            [Location(), When("Forecast availability: approximately four days; actual returned dates apply."), Details(WeatherDetailSelection.Pollution)]),
        new("get_sun_times", "Get calculated sunrise and sunset for a location and local date.",
        [
            Location(),
            new() { Name = "date", Type = FunctionArgumentType.String, Required = false, Description = "today, tomorrow or YYYY-MM-DD. Omit for today." },
        ]),
        new("get_air_pollution_history", "Get historical air quality since 27 November 2020, up to 31 days per call. AQI uses OpenWeather's 1-5 scale.",
            [Location(), HistoryDate("start", "Inclusive start."), HistoryDate("end", "Exclusive end, after start and no later than now."), Details(WeatherDetailSelection.Pollution)]),
        new("get_weather_map", "Get a weather map image for a country, continent or the world. City maps are unsupported.",
        [
            new() { Name = "layer", Description = "clouds, precipitation, pressure, wind or temp.", Type = FunctionArgumentType.String, Required = true },
            new() { Name = "location", Description = "Country name/code, continent or global. Omit for global.", Type = FunctionArgumentType.String, Required = false },
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
