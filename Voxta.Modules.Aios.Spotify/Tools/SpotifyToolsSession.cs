using System.Text.Json;
using Voxta.Abstractions.Chats.Objects.Characters;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.Scripting.ActionScripts;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.Spotify.ChatAugmentations;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.Spotify.Tools;

internal sealed class SpotifyToolsSession(ModuleToolCatalog catalog, SpotifyPlaybackSession playback)
    : IToolsChatAugmentation, IChatScriptEventsAugmentation
{
    public ServiceTypes[] GetRequiredServiceTypes() => [];
    public ValueTask<IReadOnlyList<ChatTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(catalog.GetTools());
    }
    public ValueTask<ChatToolCallResult> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken) =>
        catalog.CallToolAsync(name, arguments, cancellationToken);
    public Task OnChatScriptEvent(IActionScriptEvent e, ChatMessageData? message, ICharacterOrUserData? character, CancellationToken cancellationToken) =>
        playback.OnChatScriptEvent(e, message, character, cancellationToken);
    public ValueTask DisposeAsync() => playback.DisposeAsync();
}
