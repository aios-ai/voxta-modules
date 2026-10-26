using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services.Agents;
using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;
using System.Text.Json;

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

    private static FormArrayOfObjectsField Field => new()
    {
        Name = "SpotifyTools",
        Label = "Spotify Tools",
        Text = "Select a tool to edit the description offered to the model. Disable individual tools using their switches.",
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
                Text = "Explains to the model when and how to use this tool. An empty description uses the built-in explanation. Arguments and permissions are fixed by the module.",
            },
        ],
    };

    public static FormField ConfigurationField(ISettingsSource settings)
    {
        var field = Field;
        var current = From(settings);
        field.DefaultValue = JsonSerializer.Serialize(SpotifyToolCatalog.Choices.Select(choice => new SpotifyToolSettings
        {
            Name = choice.Name,
            Description = current.Descriptions?.GetValueOrDefault(choice.Name) ?? choice.Label,
            Disabled = (!current.Selected.Contains(choice.Name)).ToString().ToLowerInvariant(),
        }));
        return field;
    }

    public static SpotifyToolsSettings From(ISettingsSource settings)
    {
        var entries = settings.GetOptional<SpotifyToolSettings>(Field);
        var configured = (entries ?? []).Where(e => !string.IsNullOrEmpty(e.Name))
            .GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var selected = SpotifyToolCatalog.Choices.Where(choice => entries != null
                ? !configured.TryGetValue(choice.Name, out var entry) || !bool.TryParse(entry.Disabled, out var disabled) || !disabled
                : ChatToolSettingsContributions.IsToolSelected(settings, SpotifyToolCatalog.Choices, choice.Name))
            .Select(choice => choice.Name).ToHashSet(StringComparer.Ordinal);
        return new(selected, settings.GetRequired(Styles), configured
            .Where(e => !string.IsNullOrWhiteSpace(e.Value.Description))
            .ToDictionary(e => e.Key, e => e.Value.Description!.Trim(), StringComparer.Ordinal));
    }
}

internal sealed class SpotifyToolSettings
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Disabled { get; init; }
}
