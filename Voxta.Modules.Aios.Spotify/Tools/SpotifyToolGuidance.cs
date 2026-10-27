namespace Voxta.Modules.Aios.Spotify.Tools;

// Shared model guidance, published once by the playback monitor, independent of editable descriptions.
internal static class SpotifyToolGuidance
{
    public static string Build(IReadOnlySet<string> selected)
    {
        bool Has(string operation) => selected.Contains("spotify_" + operation);
        var rules = new List<string>();
        if (Has("toggle_playback")) rules.Add("A paused Spotify player can be resumed.");
        var selectionTools = new[] { "play_music", "queue_track" }.Where(Has).Select(o => "spotify_" + o).ToArray();
        if (selectionTools.Length > 0)
            rules.Add("For " + string.Join(" or ", selectionTools) + ", pass the exact Selection returned by spotify_search. Use a raw Spotify URI only when provided directly by the user."
                + (Has("search") ? " Search first when no user-supplied URI is available." : "Search is disabled; if no retained Selection or user-supplied URI is available, ask the user to supply a URI or enable search."));
        if (SpotifyToolCatalog.Choices.Any(c => selected.Contains(c.Name) && c.Name is not ("spotify_search" or "spotify_get_playlists" or "spotify_list_devices")))
            rules.Add("Trust tool results. An accepted command does not necessarily confirm playback or a requested state change. Never automatically retry state-changing commands or report unconfirmed changes as successful.");
        if (Has("list_devices") || Has("transfer_to_device"))
        {
            var capability = Has("list_devices") && Has("transfer_to_device") ? "device discovery and transfer"
                : Has("list_devices") ? "device discovery" : "device transfer";
            rules.Add("Use " + capability + " only when the user requests a different playback destination, never to recover a missing player. Use unique device names; ask for clarification or renaming when names are ambiguous.");
        }
        return string.Join(" ", rules);
    }
}
