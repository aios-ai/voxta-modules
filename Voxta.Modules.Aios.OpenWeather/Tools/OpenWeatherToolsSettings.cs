using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services.Agents;
using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.Tools;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxta.Modules.Aios.OpenWeather.Tools;

internal sealed record OpenWeatherToolsSettings(IReadOnlySet<string> Selected, ChatStyles[] ChatStyles,
    IReadOnlyDictionary<string, string>? Descriptions = null)
{
    public static readonly FormMultipleEnumField<ChatStyles> Styles = ModuleToolChatStyles.Create("OpenWeather");

    public const string CardsFieldName = "OpenWeatherToolCardsWithArguments";

    private static FormArrayOfObjectsField Field => new()
    {
        Name = CardsFieldName,
        Label = "OpenWeather Tools",
        Text = "Select a tool to edit its description or disable it. Weather and air quality each include current conditions and forecasts. On upgrade, disabling either old tool disables its combined tool; re-enable it here if wanted.",
        AllowAddRemove = false,
        AllowRename = false,
        AllowDisable = true,
        UniqueFields = [nameof(OpenWeatherToolSettings.Name)],
        FieldTemplate =
        [
            new FormTextField { Name = nameof(OpenWeatherToolSettings.Name), Label = "Name", Required = true },
            new FormMultilineField
            {
                Name = nameof(OpenWeatherToolSettings.Description), Label = "Description", Rows = 5,
            },
            ModuleToolDocumentation.ArgumentsField(),
            ModuleToolDocumentation.ArgumentsLock(),
        ],
    };

    public static FormField ConfigurationField(ISettingsSource settings)
    {
        var field = Field;
        var current = From(settings);
        field.DefaultValue = JsonSerializer.Serialize(OpenWeatherToolCatalog.Choices.Select(choice => new OpenWeatherToolSettings
        {
            Name = choice.Name,
            Arguments = OpenWeatherToolCatalog.DescribeArguments(choice.Name),
            Description = current.Descriptions?.GetValueOrDefault(choice.Name) ?? choice.Label,
            Disabled = (!current.Selected.Contains(choice.Name)).ToString().ToLowerInvariant(),
        }));
        return field;
    }

    public static OpenWeatherToolsSettings From(ISettingsSource settings)
    {
        // Read the saved cards without dropping switches or depending on JSON property casing.
        var raw = settings.GetRawValue(Field.Name);
        if (string.IsNullOrWhiteSpace(raw)) raw = settings.GetRawValue("OpenWeatherToolCards");
        var entries = string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<OpenWeatherToolSettings[]>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        // Retain saved cards from beta.1's prefixed names. An explicitly saved short name takes precedence.
        var configured = (entries ?? []).Where(e => !string.IsNullOrEmpty(e.Name))
            .GroupBy(e => CurrentName(e.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.LastOrDefault(e => e.Name == g.Key) ?? g.Last(), StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var choice in OpenWeatherToolCatalog.Choices)
        {
            configured.TryGetValue(choice.Name, out var current);
            OpenWeatherToolSettings? forecast = null;
            if (choice.Name is "get_weather" or "get_air_pollution")
                configured.TryGetValue(choice.Name + "_forecast", out forecast);
            // Preserve either old disable switch rather than silently enabling a previously disabled capability.
            var disabled = new[] { current, forecast }.Any(e => bool.TryParse(e?.Disabled, out var value) && value);
            if (!disabled && choice.EnabledByDefault) selected.Add(choice.Name);
            var description = CustomDescription(current) ?? CustomDescription(forecast);
            if (!string.IsNullOrWhiteSpace(description)) descriptions[choice.Name] = description.Trim();
        }
        return new(selected, settings.GetRequired(Styles), descriptions);
    }

    // Upgrade unchanged shipped descriptions, while retaining user-written descriptions.
    private static readonly HashSet<string> PreviousDefaultDescriptions = new(StringComparer.Ordinal)
    {
        "Get current weather for a requested city or the configured My Location, using configured units. Use details for explicitly requested fields. Returned sunrise/sunset is ONLY for the supplied date; use get_sun_times for tomorrow or another date. Never extrapolate today's sun times.",
        "Weather forecast: approximately five days ahead, three-hour samples; actual returned dates are authoritative. Prefer date/period/range/count; the module calculates local dates and DST. Weekend or week requests can exceed availability: tell the user which requested dates are unavailable, never invent or substitute forecasts. Omit time arguments for available day/night summaries. Context samples are outside the selected periods, not exact-hour predictions. Uses requested location or My Location and configured units/details. For date-specific sunrise/sunset use get_sun_times.",
        "Get calculated sunrise and sunset for a specific local date, including tomorrow, at a requested location or My Location. Uses coordinates and destination time-zone rules (including daylight saving). These are astronomical estimates, not weather observations. Use this for date-specific sun questions; do not extrapolate today's weather metadata.",
        "Get current air quality and selected pollutant concentrations for a requested city or configured My Location. AQI uses OpenWeather's 1 (good) to 5 (very poor) scale. Use details for explicit pollutant questions.",
        "Air quality forecast: approximately four days ahead, hourly samples; actual returned dates are authoritative. Prefer date/period/range/count; the module calculates local dates and DST. Weekend/week requests may exceed availability: report missing requested dates, never invent or substitute forecasts. Omit time arguments for available UTC day/night summaries. Context samples are outside selected periods, not interpolated predictions. Uses requested location or My Location and selected pollutant details. AQI is OpenWeather's 1 (good) to 5 (very poor) scale.",
        "Get historical air quality and pollutants from 27 November 2020 onward. Request up to 31 days per call; results summarize UTC day/night periods. AQI uses OpenWeather's 1 to 5 scale. Use details for explicit pollutant questions.",
        "Get a weather map image for the world, a continent or a country. Omit location for a global map. City maps are unsupported: ask for a country or continent instead. The result includes the image, geographic bounds, layer units and a color reference. When vision is available, briefly describe the visible regional pattern using the supplied legend; do not invent a summary if you cannot inspect the image.",
    };

    private static string? CustomDescription(OpenWeatherToolSettings? entry)
    {
        var text = entry?.Description?.Trim();
        return string.IsNullOrWhiteSpace(text) || PreviousDefaultDescriptions.Contains(text) ? null : text;
    }

    private static string CurrentName(string name) => name.StartsWith("openweather_", StringComparison.Ordinal)
        ? name["openweather_".Length..] : name;
}

internal sealed class OpenWeatherToolSettings
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Arguments { get; init; }
    [JsonConverter(typeof(OpenWeatherDisabledSwitchConverter))]
    public string? Disabled { get; init; }
}

internal sealed class OpenWeatherDisabledSwitchConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        JsonTokenType.String => reader.GetString(),
        _ => throw new JsonException("A tool's Disabled switch must be a boolean or string."),
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
