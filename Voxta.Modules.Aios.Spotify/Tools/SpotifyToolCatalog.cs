using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Services.Agents;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.Spotify.Tools;

internal static class SpotifyToolCatalog
{
    private const string PlayerRecovery = " Spotify context shows the active player and playing/paused state. Use an existing active player directly; an active paused player may be resumed. When Spotify reports no active player, user setup is required: ask the user to open Spotify on the intended device, select a track or playlist and start playback manually. Wait for the user to confirm playback has started before continuing the original request. Device discovery and transfer are for user-requested destination changes; they are not recovery steps for a missing active player.";
    private const string DeviceSetup = " Device discovery and transfer serve user-requested destination changes. Always use the human-readable device name in tool arguments and user-facing requests; the module resolves internal IDs privately. Ask the user to clarify or rename devices when names are ambiguous. A transfer response confirms only that Spotify accepted the transfer; if a subsequent command reports no active player, ask the user to start music manually on that device and wait for confirmation.";
    private sealed record Definition(string Operation, string Description, FunctionArgumentDefinition[] Arguments, bool Read = false);
    private static FunctionArgumentDefinition Text(string name, string description) => new()
        { Name = name, Description = description, Type = FunctionArgumentType.String, Required = true };
    private static FunctionArgumentDefinition Number(string description) => new()
        { Name = "value", Description = description, Type = FunctionArgumentType.Integer, Required = false };

    private static readonly Definition[] Definitions =
    [
        new("toggle_playback", "Toggle Spotify playback between playing and paused.", []),
        new("spotify_connect", "Legacy diagnostic query equivalent to spotify_list_devices. Lists available devices and their active/control status; it does not establish playback readiness.", [], true),
        new("play_random_music", "Play a random track from the user's top Spotify tracks.", []),
        new("play_special_playlist", "Play a configured personal Spotify playlist by its configured name.", [Text("name", "Configured special playlist name.")]),
        new("search", "Search Spotify without changing playback. Returns up to ten candidates in Spotify order with readable Selection values and identifying metadata, including release dates and their precision where available. Track dates describe the returned album edition, not necessarily the original song release. Choose the best match for the user's request, then copy its exact Selection value to spotify_play_music or spotify_queue_track; Spotify URIs are resolved privately. For a specific song by an artist, search type track with a query such as track:Song artist:Artist. For any music by an artist, search type artist. For a genre, search playlists by genre name or tracks using genre:Name. Ask the user when the results are ambiguous.", [Text("query", "Spotify search query, including artist/track/album/genre filters when useful."), Text("type", "One of: track, album, artist, playlist, show, episode.")], true),
        new("play_music", "Play an explicitly selected Spotify item. First use spotify_search to choose a matching result and copy its exact readable Selection value. This value appears in the user's approval prompt and is bound internally to the selected Spotify URI. This tool does not search or choose music. An artist selection plays that artist's context, leaving song selection to Spotify. Reports accepted versus confirmed playback and the observed track when available.", [Text("selection", "Exact readable Selection value returned by spotify_search, including artist, album, date and result number when present. Shown to the user for approval; the module resolves its URI privately. Use a raw Spotify URI only if the user supplied it directly.")]),
        new("queue_track", "Add an explicitly selected track to the Spotify queue. First use spotify_search with type track and copy its exact readable Selection value. The user approves this name; the module resolves its URI privately. This tool does not search or choose a track.", [Text("selection", "Exact readable Selection value of a track from spotify_search. Shown to the user for approval. Use a raw spotify:track: URI only if supplied directly by the user.")]),
        new("volume", "Change Spotify volume. Relative changes default to 10 percentage points.", [Text("type", "One of: set, increase, decrease."), Number("Integer 0–100. Required for set; optional for relative changes.")]),
        new("seek_playback", "Seek within the current Spotify track or episode. Use forward_percent/backward_percent for relative percentage moves from the current position (a percentage of the total duration). Use to_percent only for an absolute position measured from the beginning. For example, 'forward by 10%' uses forward_percent=10; 'go to 10%' uses to_percent=10. Ask for clarification if the intended direction or reference point is unclear.", [Text("target", "One of: forward, backward (relative seconds); forward_percent, backward_percent (relative percentage of total duration); to_time, to_percent (absolute from the beginning); middle."), Number("Nonnegative seconds for forward/backward/to_time; 0–100 for forward_percent/backward_percent/to_percent, required for percentage seeks. Relative seconds default to 10 seconds. Omit for middle.")]),
        new("skip_next", "Skip to the next Spotify track.", []),
        new("skip_previous", "Skip to the previous Spotify track.", []),
        new("repeat_mode", "Set Spotify repeat mode.", [Text("mode", "One of: track, context, off.")]),
        new("shuffle_mode", "Set Spotify shuffle mode.", [Text("mode", "One of: on, off.")]),
        new("add_to_favorites", "Save the current Spotify track to Liked Songs.", []),
        new("get_playlists", "List the user's available Spotify playlists.", [], true),
        new("add_to_playlist", "Add the current Spotify track to an existing playlist.", [Text("playlist", "Exact playlist name from the user's available playlists.")]),
        new("list_devices", "List Spotify Connect device names, types, active status and control availability when the user wants to change playback destination. Use before a requested transfer to find the destination's name.", [], true),
        new("transfer_to_device", "Transfer Spotify playback when the user requests a different device. First use spotify_list_devices to find the destination name. Preserves the current playing or paused state. A successful transfer does not establish that playback control is ready.", [Text("device", "Human-readable device name from spotify_list_devices, for example XENTOWER or Living Room. This name is shown in the user's approval prompt. Internal device IDs are resolved privately by the module.")]),
    ];

    public static readonly ChatToolChoice[] Choices = Definitions.Select(d =>
        new ChatToolChoice("spotify_" + d.Operation.Replace("spotify_", ""), d.Description, true)).ToArray();

    public static ModuleToolCatalog Create(SpotifyToolHandler handler, SpotifyToolsSettings settings,
        IReadOnlyDictionary<string, string> playlists, ILogger logger) => new(
        Definitions.Select((definition, index) => new ModuleTool(
            new ChatTool(Choices[index].Name,
                (settings.Descriptions?.GetValueOrDefault(Choices[index].Name) ?? definition.Description)
                + (definition.Operation is "play_music" or "queue_track" ? " Arguments accept the exact readable Selection value from spotify_search, not a search query or an invented name. This text is shown for approval and resolved privately to one URI. Only use a raw Spotify URI when the user supplied it directly. An accepted_unconfirmed playback result acknowledges the command without confirming it started; do not report it as a failed command or retry it automatically." : "")
                + (NeedsPlayer(definition.Operation) ? PlayerRecovery : "") + (definition.Operation is "list_devices" or "transfer_to_device" ? DeviceSetup : "") + (definition.Operation == "play_special_playlist"
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
