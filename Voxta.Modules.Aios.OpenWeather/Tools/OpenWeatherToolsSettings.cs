using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services.Agents;
using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxta.Modules.Aios.OpenWeather.Tools;

internal sealed record OpenWeatherToolsSettings(IReadOnlySet<string> Selected, ChatStyles[] ChatStyles,
    IReadOnlyDictionary<string, string>? Descriptions = null)
{
    public static readonly FormMultipleEnumField<ChatStyles> Styles = new()
    {
        Name = ChatToolSettingsContributions.ToolChatStyles.Name,
        Label = ChatToolSettingsContributions.ToolChatStyles.Label,
        Choices = ChatToolSettingsContributions.ToolChatStyles.Choices,
        StartValues = [Voxta.Model.Shared.ChatStyles.Assistant, Voxta.Model.Shared.ChatStyles.Companion],
        Text = "Offer OpenWeather tools in these chat styles. Defaults to Assistant and Companion.",
    };

    private static FormArrayOfObjectsField Field => new()
    {
        Name = "OpenWeatherToolCards",
        Label = "OpenWeather Tools",
        Text = "Select a tool to edit the description offered to the model. Disable individual tools using their switches.",
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
                Text = "Explains to the model when and how to use this tool. An empty description uses the built-in explanation. Arguments and permissions are fixed by the module.",
            },
        ],
    };

    public static FormField ConfigurationField(ISettingsSource settings)
    {
        var field = Field;
        var current = From(settings);
        field.DefaultValue = JsonSerializer.Serialize(OpenWeatherToolCatalog.Choices.Select(choice => new OpenWeatherToolSettings
        {
            Name = choice.Name,
            Description = current.Descriptions?.GetValueOrDefault(choice.Name) ?? choice.Label,
            Disabled = (!current.Selected.Contains(choice.Name)).ToString().ToLowerInvariant(),
        }));
        return field;
    }

    public static OpenWeatherToolsSettings From(ISettingsSource settings)
    {
        // Read the saved cards without dropping switches or depending on JSON property casing.
        var raw = settings.GetRawValue(Field.Name);
        var entries = string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<OpenWeatherToolSettings[]>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var configured = (entries ?? []).Where(e => !string.IsNullOrEmpty(e.Name))
            .GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var selected = OpenWeatherToolCatalog.Choices.Where(choice => configured.TryGetValue(choice.Name, out var entry)
                ? !bool.TryParse(entry.Disabled, out var disabled) || !disabled
                : choice.EnabledByDefault)
            .Select(choice => choice.Name).ToHashSet(StringComparer.Ordinal);
        return new(selected, settings.GetRequired(Styles), configured
            .Where(e => !string.IsNullOrWhiteSpace(e.Value.Description))
            .ToDictionary(e => e.Key, e => e.Value.Description!.Trim(), StringComparer.Ordinal));
    }
}

internal sealed class OpenWeatherToolSettings
{
    public required string Name { get; init; }
    public string? Description { get; init; }
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
