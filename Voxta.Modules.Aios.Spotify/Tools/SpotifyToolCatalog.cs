using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Services.Agents;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.Spotify.Tools;

// Tool descriptions are intentionally limited to tool selection and argument semantics.
// Shared policies (player availability, no automatic retries, accepted vs confirmed,
// search -> exact Selection, and transfer-only-on-request) belong in Spotify context.
internal static class SpotifyToolCatalog
{
    private sealed record Definition(string Operation, string Description, FunctionArgumentDefinition[] Arguments, bool Read = false);

    private static FunctionArgumentDefinition Text(string name, string description) => new()
        { Name = name, Description = description, Type = FunctionArgumentType.String, Required = true };

    private static FunctionArgumentDefinition Number(string description) => new()
        { Name = "value", Description = description, Type = FunctionArgumentType.Integer, Required = false };

    private static readonly Definition[] Definitions =
    [
        new("toggle_playback", "Pause or resume the active player.", []),
        new("play_random_music", "Play a random track from the user's top tracks.", []),
        new("play_special_playlist", "Play a configured personal playlist.",
            [Text("name", "Configured playlist name.")]),

        new("search", "Search without changing playback; ask if matches are ambiguous. For any music by an artist, use type=artist; for a specific song, use type=track. Dates refer to the listed edition, not necessarily the original song release.",
            [Text("query", "Search text; filters include track: and artist:. For genres, use genre: with type=track, or a playlist name."),
             Text("type", "track, album, artist, playlist, show, or episode.")], true),
        new("play_music", "Play a selected Spotify item; an artist selection lets Spotify choose songs. Search first unless the user supplied its URI.",
            [Text("selection", "Exact Selection from spotify_search, or a Spotify URI supplied by the user.")]),
        new("queue_track", "Queue a selected track; search first unless the user supplied its URI.",
            [Text("selection", "Exact track Selection from spotify_search, or a user-supplied spotify:track: URI.")]),

        new("volume", "Set or adjust the active player's volume.",
            [Text("type", "set, increase, or decrease."),
             Number("0–100; required for set; relative changes default to 10 percentage points.")]),
        new("seek_playback", "Seek within the current track or episode.",
            [Text("target", "forward/backward: relative seconds; forward_percent/backward_percent: relative % of total duration; to_time: absolute seconds; to_percent: absolute %; middle: midpoint."),
             Number("Nonnegative integer seconds or 0–100 percent; required except forward/backward (default 10 seconds) and middle (omit).")]),
        new("skip", "Skip to the next or previous track.", [Text("direction", "next or previous.")]),
        new("repeat_mode", "Set repeat mode.", [Text("mode", "track: repeat the current track; context: repeat the current playback collection, such as the current album or playlist; off: disable repeat.")]),
        new("shuffle_mode", "Turn shuffle on or off.", [Text("mode", "on or off.")]),

        new("add_to_favorites", "Save the current track to Liked Songs.", []),
        new("get_playlists", "List the user's playlists.", [], true),
        new("add_to_playlist", "Add the current track to an existing playlist.",
            [Text("playlist", "Playlist name from spotify_get_playlists.")]),

        new("list_devices", "List Spotify Connect devices and whether they can be controlled.", [], true),
        new("transfer_to_device", "Transfer playback to a requested device; retain playing/paused state.",
            [Text("device", "Unique device name from spotify_list_devices (not a device ID).")]),
    ];

    public static readonly ChatToolChoice[] Choices = Definitions.Select(d =>
        new ChatToolChoice("spotify_" + d.Operation, d.Description,
            d.Operation is not ("list_devices" or "transfer_to_device"))).ToArray();

    public static string DescribeArguments(string name) =>
        ModuleToolDocumentation.Arguments(Definitions[Array.FindIndex(Choices, choice => choice.Name == name)].Arguments);

    public static ModuleToolCatalog Create(SpotifyToolHandler handler, SpotifyToolsSettings settings,
        IReadOnlyDictionary<string, string> playlists, ILogger logger) => new(
        Definitions.Select((definition, index) => new ModuleTool(
            new ChatTool(Choices[index].Name,
                (settings.Descriptions?.GetValueOrDefault(Choices[index].Name) ?? definition.Description)
                + (definition.Operation == "play_special_playlist"
                    ? " Configured names: " + (playlists.Count == 0 ? "none" : string.Join(", ", playlists.Keys)) + "."
                    : ""),
                ChatToolSchema.FromArguments(definition.Arguments), VoxtaModule.ServiceName)
            {
                AugmentationKey = VoxtaModule.ToolsAugmentationKey,
                Enabled = settings.Selected.Contains(Choices[index].Name),
                ChatStyles = settings.ChatStyles,
                Permission = definition.Read ? ToolPermissionClass.Read : ToolPermissionClass.Act,
                ExternalContent = true,
            },
            (arguments, token) => handler.ExecuteAsync(definition.Operation, arguments, token))), logger);
}
