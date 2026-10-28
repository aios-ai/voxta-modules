using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services.Agents;
using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.Tools;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxta.Modules.Aios.Spotify.Tools;

internal sealed record SpotifyToolsSettings(IReadOnlySet<string> Selected, ChatStyles[] ChatStyles,
    IReadOnlyDictionary<string, string>? Descriptions = null)
{
    public static readonly FormMultipleEnumField<ChatStyles> Styles = new()
    {
        Name = ChatToolSettingsContributions.ToolChatStyles.Name,
        Label = ChatToolSettingsContributions.ToolChatStyles.Label,
        Choices = ChatToolSettingsContributions.ToolChatStyles.Choices,
        StartValues = [Voxta.Model.Shared.ChatStyles.Assistant, Voxta.Model.Shared.ChatStyles.Companion],
        Text = "Offer Spotify tools in these chat styles. Defaults to Assistant and Companion.",
    };

    public const string CardsFieldName = "SpotifyToolsWithArgumentsV2";

    private static FormArrayOfObjectsField Field => new()
    {
        Name = CardsFieldName,
        Label = "Spotify Tools",
        Text = "Select a tool to edit the description offered to the model. Disable individual tools using their switches. Arguments below the description show the inputs this tool accepts.",
        AllowAddRemove = false,
        AllowRename = false,
        AllowDisable = true,
        UniqueFields = [nameof(SpotifyToolSettings.Name)],
        FieldTemplate =
        [
            new FormTextField { Name = nameof(SpotifyToolSettings.Name), Label = "Name", Required = true },
            new FormMultilineField
            {
                Name = nameof(SpotifyToolSettings.Description), Label = "Description", Rows = 5,
            },
            ModuleToolDocumentation.ArgumentsField(),
            ModuleToolDocumentation.ArgumentsLock(),
        ],
    };

    public static FormField ConfigurationField(ISettingsSource settings)
    {
        var field = Field;
        var current = From(settings);
        field.DefaultValue = JsonSerializer.Serialize(SpotifyToolCatalog.Choices.Select(choice => new SpotifyToolSettings
        {
            Name = choice.Name,
            Arguments = SpotifyToolCatalog.DescribeArguments(choice.Name),
            Description = current.Descriptions?.GetValueOrDefault(choice.Name) ?? choice.Label,
            Disabled = (!current.Selected.Contains(choice.Name)).ToString().ToLowerInvariant(),
        }));
        return field;
    }

    public static SpotifyToolsSettings From(ISettingsSource settings)
    {
        // Read the saved cards without dropping switches or depending on JSON property casing.
        var raw = settings.GetRawValue(Field.Name);
        if (string.IsNullOrWhiteSpace(raw)) raw = settings.GetRawValue("SpotifyToolsWithArguments");
        if (string.IsNullOrWhiteSpace(raw)) raw = settings.GetRawValue("SpotifyTools");
        var entries = string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<SpotifyToolSettings[]>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var configured = (entries ?? []).Where(e => !string.IsNullOrEmpty(e.Name))
            .GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        // A merged switch must never undo an explicit opt-out of either old direction.
        var legacySkipNames = new[] { "spotify_skip_next", "spotify_skip_previous" };
        if (!configured.ContainsKey("spotify_skip") && legacySkipNames.Any(configured.ContainsKey))
            configured["spotify_skip"] = new SpotifyToolSettings
            {
                Name = "spotify_skip",
                Disabled = legacySkipNames.Any(name => configured.TryGetValue(name, out var old)
                    && bool.TryParse(old.Disabled, out var disabled) && disabled) ? "true" : "false",
            };
        var selected = SpotifyToolCatalog.Choices.Where(choice => entries != null
                ? configured.TryGetValue(choice.Name, out var entry)
                    ? !bool.TryParse(entry.Disabled, out var disabled) || !disabled
                    : choice.EnabledByDefault
                : ChatToolSettingsContributions.IsToolSelected(settings, SpotifyToolCatalog.Choices, choice.Name))
            .Select(choice => choice.Name).ToHashSet(StringComparer.Ordinal);
        var legacySelection = settings.GetRawValue("Tools");
        if (entries == null && !string.IsNullOrWhiteSpace(legacySelection)
            && legacySkipNames.Any(name => legacySelection.Contains(name, StringComparison.Ordinal)))
        {
            var legacyChoices = legacySkipNames.Select(name => new ChatToolChoice(name, name, true)).ToArray();
            if (legacySkipNames.All(name => ChatToolSettingsContributions.IsToolSelected(settings, legacyChoices, name)))
                selected.Add("spotify_skip");
        }
        return new(selected, settings.GetRequired(Styles), configured
            .Where(e => !string.IsNullOrWhiteSpace(e.Value.Description))
            .ToDictionary(e => e.Key, e => e.Value.Description!.Trim(), StringComparer.Ordinal));
    }
}

internal sealed class SpotifyToolSettings
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Arguments { get; init; }
    [JsonConverter(typeof(SpotifyDisabledSwitchConverter))]
    public string? Disabled { get; init; }
}

internal sealed class SpotifyDisabledSwitchConverter : JsonConverter<string>
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
