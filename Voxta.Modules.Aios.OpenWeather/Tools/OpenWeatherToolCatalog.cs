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
        Description = "City and optional state/country, preserving punctuation. Omit to use My Location from settings. If no location is configured, ask the user. Do not guess a location.",
    };
    private static readonly Definition[] Definitions =
    [
        new("get_weather", "Get current weather for a requested city or the configured My Location, using the configured units and detail selection.", [Location()]),
        new("get_weather_forecast", "Get the available weather forecast, summarized into day/night periods for up to five days. Use the requested city or configured My Location and configured units/details.", [Location()]),
        new("get_air_pollution", "Get current air quality and selected pollutant concentrations for a requested city or configured My Location. AQI uses OpenWeather's 1 (good) to 5 (very poor) scale.", [Location()]),
        new("get_air_pollution_forecast", "Get the available air quality forecast for up to five days, using selected pollutant details. Use the requested city or configured My Location. AQI uses OpenWeather's 1 to 5 scale.", [Location()]),
        new("get_weather_map", "Get a weather map image for the world, a continent or a country. Omit location for a global map. City maps are unsupported: ask for a country or continent instead. The result includes the image, geographic bounds, layer units and a color reference. When vision is available, briefly describe the visible regional pattern using the supplied legend; do not invent a summary if you cannot inspect the image.",
        [
            new() { Name = "layer", Description = "Requested weather layer: clouds, precipitation, pressure, wind or temp.", Type = FunctionArgumentType.String, Required = true },
            new() { Name = "location", Description = "Country name or ISO alpha-2 code, continent name, or global. Omit for global. This does not use My Location.", Type = FunctionArgumentType.String, Required = false },
        ]),
    ];

    public static readonly ChatToolChoice[] Choices = Definitions.Select(d => new ChatToolChoice(d.Name, d.Description, true)).ToArray();

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
