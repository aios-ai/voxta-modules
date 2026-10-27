using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services.Agents;
using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxta.Modules.Aios.PhilipsHue.Tools;

internal sealed record HueToolsSettings(IReadOnlySet<string> Selected, ChatStyles[] ChatStyles,
    IReadOnlyDictionary<string, string>? Descriptions = null)
{
    public static readonly FormMultipleEnumField<ChatStyles> Styles = new()
    {
        Name = ChatToolSettingsContributions.ToolChatStyles.Name,
        Label = ChatToolSettingsContributions.ToolChatStyles.Label,
        Choices = ChatToolSettingsContributions.ToolChatStyles.Choices,
        StartValues = [Voxta.Model.Shared.ChatStyles.Assistant, Voxta.Model.Shared.ChatStyles.Companion],
        Text = "Offer Hue tools in these chat styles. Defaults to Assistant and Companion.",
    };

    private static FormArrayOfObjectsField Field => new()
    {
        Name = "HueTools",
        Label = "Hue Tools",
        Text = "Select a tool to edit the description offered to the model. Disable individual tools using their switches.",
        AllowAddRemove = false,
        AllowRename = false,
        AllowDisable = true,
        UniqueFields = [nameof(HueToolSettings.Name)],
        FieldTemplate =
        [
            new FormTextField { Name = nameof(HueToolSettings.Name), Label = "Name", Required = true },
            new FormMultilineField
            {
                Name = nameof(HueToolSettings.Description), Label = "Description", Rows = 5,
                Text = "Explains to the model when and how to use this tool. An empty description uses the built-in explanation. Arguments and permissions are fixed by the module.",
            },
        ],
    };

    public static FormField ConfigurationField(ISettingsSource settings)
    {
        var field = Field;
        var current = From(settings);
        field.DefaultValue = JsonSerializer.Serialize(HueToolCatalog.Choices.Select(choice => new HueToolSettings
        {
            Name = choice.Name,
            Description = current.Descriptions?.GetValueOrDefault(choice.Name) ?? choice.Label,
            Disabled = (!current.Selected.Contains(choice.Name)).ToString().ToLowerInvariant(),
        }));
        return field;
    }

    public static HueToolsSettings From(ISettingsSource settings)
    {
        // Read the saved cards without dropping switches or depending on JSON property casing.
        var raw = settings.GetRawValue(Field.Name);
        var entries = string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<HueToolSettings[]>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var configured = (entries ?? []).Where(e => !string.IsNullOrEmpty(e.Name))
            .GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var oldNames = new[] { "hue_turn_lights_on", "hue_turn_lights_off" };
        if (!configured.ContainsKey("hue_toggle_lights") && oldNames.Any(configured.ContainsKey))
            configured["hue_toggle_lights"] = new HueToolSettings
            {
                Name = "hue_toggle_lights",
                Disabled = oldNames.Any(name => configured.TryGetValue(name, out var old)
                    && bool.TryParse(old.Disabled, out var disabled) && disabled) ? "true" : "false",
            };
        var selected = HueToolCatalog.Choices.Where(choice => entries != null
                ? configured.TryGetValue(choice.Name, out var entry)
                    ? !bool.TryParse(entry.Disabled, out var disabled) || !disabled
                    : choice.EnabledByDefault
                : ChatToolSettingsContributions.IsToolSelected(settings, HueToolCatalog.Choices, choice.Name))
            .Select(choice => choice.Name).ToHashSet(StringComparer.Ordinal);
        var oldSelection = settings.GetRawValue("Tools");
        if (entries == null && !string.IsNullOrWhiteSpace(oldSelection)
            && oldNames.Any(name => oldSelection.Contains(name, StringComparison.Ordinal)))
        {
            var oldChoices = oldNames.Select(name => new ChatToolChoice(name, name, true)).ToArray();
            if (oldNames.All(name => ChatToolSettingsContributions.IsToolSelected(settings, oldChoices, name)))
                selected.Add("hue_toggle_lights");
            else selected.Remove("hue_toggle_lights");
        }
        return new(selected, settings.GetRequired(Styles), configured
            .Where(e => !string.IsNullOrWhiteSpace(e.Value.Description))
            .ToDictionary(e => e.Key, e => e.Value.Description!.Trim(), StringComparer.Ordinal));
    }
}

internal sealed class HueToolSettings
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    [JsonConverter(typeof(HueDisabledSwitchConverter))]
    public string? Disabled { get; init; }
}

internal sealed class HueDisabledSwitchConverter : JsonConverter<string>
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
