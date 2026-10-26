using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services.Agents;
using Voxta.Model.Shared;

namespace Voxta.Modules.Aios.Spotify.Tools;

internal sealed record SpotifyToolsSettings(IReadOnlySet<string> Selected, ChatStyles[] ChatStyles)
{
    public static SpotifyToolsSettings From(ISettingsSource settings) => new(
        SpotifyToolCatalog.Choices.Where(choice => ChatToolSettingsContributions.IsToolSelected(settings, SpotifyToolCatalog.Choices, choice.Name))
            .Select(choice => choice.Name).ToHashSet(StringComparer.Ordinal),
        ChatToolSettingsContributions.GetChatStyles(settings));
}
