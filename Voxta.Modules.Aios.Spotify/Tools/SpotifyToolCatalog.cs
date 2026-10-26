using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Services.Agents;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.Spotify.Tools;

internal static class SpotifyToolCatalog
{
    private const string PlayerRecovery = " Requires an active Spotify player, which may be paused. If no player is active, use spotify_list_devices, then spotify_transfer_to_device, then retry this command once after a successful transfer. Select a controllable device matching the user's explicit name or device type, or the only controllable device. If several devices could match, ask the user which one; never infer their computer name or location. If no device is available, ask the user to open Spotify and briefly start playback manually.";
    private sealed record Definition(string Operation, string Description, FunctionArgumentDefinition[] Arguments, bool Read = false);
    private static FunctionArgumentDefinition Text(string name, string description) => new()
        { Name = name, Description = description, Type = FunctionArgumentType.String, Required = true };
    private static FunctionArgumentDefinition Number(string description) => new()
        { Name = "value", Description = description, Type = FunctionArgumentType.Integer, Required = false };

    private static readonly Definition[] Definitions =
    [
        new("toggle_playback", "Toggle Spotify playback between playing and paused.", []),
        new("spotify_connect", "Check available Spotify Connect players and their active/control status. This is a diagnostic query; it does not activate a player. Use spotify_transfer_to_device to activate a chosen available device.", [], true),
        new("play_random_music", "Play a random track from the user's top Spotify tracks.", []),
        new("play_special_playlist", "Play a configured personal Spotify playlist by its configured name.", [Text("name", "Configured special playlist name.")]),
        new("play_music", "Search Spotify and play the requested music or podcast.", [Text("name", "Track, artist, album, playlist, show, episode, or genre to search for."), Text("type", "One of: track, album, artist, playlist, show, episode, genre.")]),
        new("queue_track", "Find a track and add it to the Spotify queue.", [Text("name", "Track name and artist, with album if known.")]),
        new("volume", "Change Spotify volume. Relative changes default to 10 percentage points.", [Text("type", "One of: set, increase, decrease."), Number("Integer 0–100. Required for set; optional for relative changes.")]),
        new("seek_playback", "Seek within the current Spotify track or episode.", [Text("target", "One of: forward, backward, to_time, to_percent, middle."), Number("Nonnegative seconds for forward/backward/to_time; 0–100 for to_percent. Relative seeks default to 10 seconds. Omit for middle.")]),
        new("skip_next", "Skip to the next Spotify track.", []),
        new("skip_previous", "Skip to the previous Spotify track.", []),
        new("repeat_mode", "Set Spotify repeat mode.", [Text("mode", "One of: track, context, off.")]),
        new("shuffle_mode", "Set Spotify shuffle mode.", [Text("mode", "One of: on, off.")]),
        new("add_to_favorites", "Save the current Spotify track to Liked Songs.", []),
        new("get_playlists", "List the user's available Spotify playlists.", [], true),
        new("add_to_playlist", "Add the current Spotify track to an existing playlist.", [Text("playlist", "Exact playlist name from the user's available playlists.")]),
        new("list_devices", "Discover Spotify Connect devices, including name, device type, active status, control availability and ID. Available inactive devices can be activated with spotify_transfer_to_device without manually starting music. Match the user's requested device name or type; ask which device when several match. Do not guess the user's computer name.", [], true),
        new("transfer_to_device", "Activate or transfer playback to a controllable device from spotify_list_devices. Use the user's chosen device or the only suitable device; ask when ambiguous. Preserves the current playing or paused state. After success, run the originally requested playback command; transfer alone does not start the requested music.", [Text("device", "Device ID, exact device name, or an unambiguous part of its name from spotify_list_devices.")]),
    ];

    public static readonly ChatToolChoice[] Choices = Definitions.Select(d =>
        new ChatToolChoice("spotify_" + d.Operation.Replace("spotify_", ""), d.Description, true)).ToArray();

    public static ModuleToolCatalog Create(SpotifyToolHandler handler, SpotifyToolsSettings settings,
        IReadOnlyDictionary<string, string> playlists, ILogger logger) => new(
        Definitions.Select((definition, index) => new ModuleTool(
            new ChatTool(Choices[index].Name,
                (settings.Descriptions?.GetValueOrDefault(Choices[index].Name) ?? definition.Description) + (NeedsPlayer(definition.Operation) ? PlayerRecovery : "") + (definition.Operation == "play_special_playlist"
                    ? " Configured playlists: " + (playlists.Count == 0 ? "none" : string.Join(", ", playlists.Keys)) + "." : ""),
                ChatToolSchema.FromArguments(definition.Arguments), VoxtaModule.ServiceName)
            {
                AugmentationKey = VoxtaModule.ToolsAugmentationKey,
                Enabled = settings.Selected.Contains(Choices[index].Name),
                ChatStyles = settings.ChatStyles,
                Permission = definition.Read ? ToolPermissionClass.Read : ToolPermissionClass.Act,
                ExternalContent = true,
            },
            (arguments, token) => handler.ExecuteAsync(definition.Operation, arguments, token))), logger);

    private static bool NeedsPlayer(string operation) => operation is "toggle_playback" or "play_random_music" or "play_special_playlist"
        or "play_music" or "queue_track" or "volume" or "seek_playback" or "skip_next" or "skip_previous" or "repeat_mode" or "shuffle_mode";
}
