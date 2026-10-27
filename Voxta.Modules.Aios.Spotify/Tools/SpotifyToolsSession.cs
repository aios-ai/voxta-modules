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
        // Voxta asks for definitions on each generation. Keep saved switches authoritative;
        // player-independent queries and explicit transfers remain useful without active playback.
        var hasPlayer = playback.HasActivePlayer;
        IReadOnlyList<ChatTool> tools = catalog.GetTools().Select(tool => tool with
        {
            Enabled = tool.Enabled && (hasPlayer || tool.Name is
                "spotify_search" or "spotify_get_playlists" or "spotify_list_devices" or "spotify_transfer_to_device"),
        }).ToArray();
        return ValueTask.FromResult(tools);
    }
    public ValueTask<ChatToolCallResult> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken) =>
        catalog.CallToolAsync(name, arguments, cancellationToken);
    public Task OnChatScriptEvent(IActionScriptEvent e, ChatMessageData? message, ICharacterOrUserData? character, CancellationToken cancellationToken) =>
        playback.OnChatScriptEvent(e, message, character, cancellationToken);
    public ValueTask DisposeAsync() => playback.DisposeAsync();
}
