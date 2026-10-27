using System.Text.Json;
using SpotifyAPI.Web;
using Voxta.Abstractions.Services.Agents;
using Voxta.Modules.Aios.Spotify.Clients.Services;
using Voxta.Modules.Aios.Spotify.Helpers;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.Spotify.Tools;

internal sealed class SpotifyToolHandler(
    ISpotifyManager manager,
    IReadOnlyDictionary<string, string> specialPlaylists,
    SpotifySpeechDucking ducking)
{
    // Serialize commands; never retry a side effect.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SpotifyToolSelections _selections = new();
    private static readonly JsonSerializerOptions SearchJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public async ValueTask<ChatToolCallResult> ExecuteAsync(string operation, JsonElement? args, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await ExecuteCoreAsync(operation, args, token); }
        finally { _gate.Release(); }
    }

    private async Task<ChatToolCallResult> ExecuteCoreAsync(string operation, JsonElement? args, CancellationToken token)
    {
        switch (operation)
        {
            case "toggle_playback":
            {
                var state = await manager.GetCurrentPlaybackState(token);
                if (state?.Device == null) return Failure(SpotifyManager.NoActiveDeviceMessage);
                return await Changed(await manager.ControlSpotifyPlayback(!state.IsPlaying, token), state.IsPlaying ? "Paused playback." : "Resumed playback.", token);
            }
            case "play_random_music":
            {
                var tracks = await manager.GetUsersTopTracks(token);
                if (tracks?.Items is not { Count: > 0 }) return Failure("No top tracks were found.");
                var track = tracks.Items[Random.Shared.Next(tracks.Items.Count)];
                return await Play(track.Uri, "track", token);
            }
            case "play_special_playlist":
            {
                var name = Text(args, "name");
                var match = specialPlaylists.FirstOrDefault(p => StringUtils.NormaliseKey(p.Key) == StringUtils.NormaliseKey(name));
                if (match.Key == null) return new("That special playlist is not configured. Available names: " + string.Join(", ", specialPlaylists.Keys), true);
                return await Play($"spotify:playlist:{match.Value}", "playlist", token);
            }
            case "search":
                return await Search(args, token);
            case "play_music":
            case "queue_track":
            {
                var selection = Text(args, "selection");
                var selected = _selections.Resolve(selection);
                var uri = selected?.Uri ?? selection;
                var parts = uri.Split(':');
                if (parts.Length != 3 || parts[0] != "spotify" || !new[] { "track", "album", "artist", "playlist", "show", "episode" }.Contains(parts[1])
                    || parts[2].Length != 22 || !parts[2].All(char.IsAsciiLetterOrDigit))
                    throw new ModuleToolInputException("That selection is unknown or expired. Run spotify_search and copy the exact readable Selection value. A Spotify URI is accepted only when supplied directly by the user.");
                if (operation == "queue_track" && parts[1] != "track")
                    throw new ModuleToolInputException("Only spotify:track: URIs can be queued with this tool.");
                return operation == "queue_track"
                    ? await Changed(await manager.QueueTrack(uri, token), $"Spotify accepted the queue request for {selected?.Selection ?? uri}.", token)
                    : await Play(uri, parts[1], token);
            }
            case "volume":
            {
                var type = Choice(args, "type", "set", "increase", "decrease");
                var value = Number(args, type == "set" ? null : 10, 100);
                var state = await manager.GetCurrentPlaybackState(token);
                if (state?.Device == null) return Failure(SpotifyManager.NoActiveDeviceMessage);
                return await Changed(await ducking.ChangeVolumeAsync(type, value, token, state.Device.VolumePercent), $"Volume change applied: {type} {value}%.", token);
            }
            case "seek_playback":
            {
                var target = Choice(args, "target", "forward", "backward", "forward_percent", "backward_percent", "to_time", "to_percent", "middle");
                var percentage = target is "forward_percent" or "backward_percent" or "to_percent";
                var value = target == "middle" ? 0 : Number(args, target is "forward" or "backward" ? 10 : null, percentage ? 100 : int.MaxValue);
                var state = await manager.GetCurrentPlaybackState(token);
                var duration = state?.Item switch { FullTrack track => track.DurationMs, FullEpisode episode => episode.DurationMs, _ => 0 };
                if (state == null || duration <= 0) return Failure("No track or episode is currently available to seek.");
                long position = target switch
                {
                    "forward" => state.ProgressMs + (long)value * 1000,
                    "backward" => state.ProgressMs - (long)value * 1000,
                    "forward_percent" => state.ProgressMs + (long)duration * value / 100,
                    "backward_percent" => state.ProgressMs - (long)duration * value / 100,
                    "to_time" => (long)value * 1000,
                    "to_percent" => (long)duration * value / 100,
                    _ => duration / 2,
                };
                var clamped = (int)Math.Clamp(position, 0, duration);
                return await Changed(await manager.SeekPlayback(clamped, token), $"Playback position set to {StringUtils.FormatMillisecondsToMinutesSeconds(clamped)}.", token);
            }
            case "skip_next":
            case "skip_previous":
                return await Changed(await manager.SkipToPreviousOrNextTrack(operation == "skip_next" ? "next" : "previous", token), operation == "skip_next" ? "Skipped to the next track." : "Skipped to the previous track.", token);
            case "repeat_mode":
            {
                var mode = Choice(args, "mode", "track", "context", "off");
                return await Changed(await manager.SetRepeatMode(mode, token), $"Repeat mode set to {mode}.", token);
            }
            case "shuffle_mode":
            {
                var mode = Choice(args, "mode", "on", "off");
                return await Changed(await manager.SetShuffle(mode == "on", token), $"Shuffle set to {mode}.", token);
            }
            case "get_playlists":
            {
                var playlists = await manager.ListAvailablePlaylists(token);
                return manager.LastUserVisibleError != null ? Failure("Could not list playlists.") : new("Available playlists: " + (playlists.Count == 0 ? "none" : string.Join(", ", playlists.Keys)), false);
            }
            case "list_devices":
                return await DescribeDevices(token);
            case "transfer_to_device":
            {
                var name = Text(args, "device");
                var devices = await manager.GetAvailablePlaybackDevices(token);
                if (manager.LastUserVisibleError != null) return Failure("Could not list devices.");
                var matches = devices.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 0) matches = devices.Where(d => d.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true).ToArray();
                if (matches.Length != 1) return new(matches.Length == 0 ? "No matching device name was found. Choose a name from spotify_list_devices." : "Several Spotify devices match that name. Choose a distinct name, or rename identically named devices in Spotify before transferring. Matching devices: " + FormatDevices(matches), true);
                var device = matches[0];
                if (device.IsRestricted || string.IsNullOrWhiteSpace(device.Id)) return new("That Spotify device cannot be controlled through the Web API. Choose another available device.", true);
                return await Changed(await manager.TransferPlayback(device.Id, token), $"Transferred playback to {device.Name}. Transfer preserves the current playing or paused state; it does not confirm that the requested music has started.", token);
            }
            case "add_to_favorites":
            case "add_to_playlist":
            {
                var name = operation == "add_to_playlist" ? Text(args, "playlist") : null;
                var state = await manager.GetCurrentPlaybackState(token);
                if (state?.Item is not FullTrack track) return Failure("No Spotify track is currently available.");
                if (name == null)
                {
                    var saved = await manager.AddTrackToLibraryAsync(track.Uri, TrackName(track), token);
                    if (saved == SpotifyLibrarySaveResult.AlreadySaved)
                        return new($"{TrackName(track)} is already in Liked Songs. No change was needed.", false);
                    return await Changed(saved == SpotifyLibrarySaveResult.Saved, $"Saved {TrackName(track)} to Liked Songs.", token);
                }
                var playlists = await manager.ListAvailablePlaylists(token);
                if (manager.LastUserVisibleError != null) return Failure("Could not list playlists.");
                var match = playlists.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match.Key == null) return new("That playlist was not found. Use an exact name from the playlist list.", true);
                return await Changed(await manager.AddItems(StringUtils.ExtractIdFromUri(match.Value), new PlaylistAddItemsRequest([track.Uri]), token), $"Added {TrackName(track)} to {match.Key}.", token);
            }
            default: return new("Unsupported Spotify operation.", true);
        }
    }

    private async Task<ChatToolCallResult> Search(JsonElement? args, CancellationToken token)
    {
        var query = Text(args, "query");
        var type = Choice(args, "type", "track", "album", "artist", "playlist", "show", "episode");
        var searchType = type switch
        {
            "track" => SearchRequest.Types.Track, "album" => SearchRequest.Types.Album,
            "artist" => SearchRequest.Types.Artist, "playlist" => SearchRequest.Types.Playlist,
            "show" => SearchRequest.Types.Show, _ => SearchRequest.Types.Episode,
        };
        var response = await manager.SearchSpotify(query, searchType, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (response == null) return Failure("Spotify search could not be completed.");
        // Keep Spotify's ordering and metadata; no scoring, history suppression or automatic selection.
        IEnumerable<SpotifySearchItem> results = type switch
        {
            "track" => (response.Tracks?.Items ?? []).Where(t => t != null).Select(t => new SpotifySearchItem(t.Uri, type, t.Name, t.Artists?.Select(a => a.Name).ToArray(), t.Album?.Name, t.Album?.ReleaseDate, t.Album?.ReleaseDatePrecision)),
            "album" => (response.Albums?.Items ?? []).Where(a => a != null).Select(a => new SpotifySearchItem(a.Uri, type, a.Name, a.Artists?.Select(x => x.Name).ToArray(), ReleaseDate: a.ReleaseDate, ReleaseDatePrecision: a.ReleaseDatePrecision)),
            "artist" => (response.Artists?.Items ?? []).Where(a => a != null).Select(a => new SpotifySearchItem(a.Uri, type, a.Name)),
            "playlist" => (response.Playlists?.Items ?? []).Where(p => p != null).Select(p => new SpotifySearchItem(p.Uri ?? "", type, p.Name ?? "", Owner: p.Owner?.DisplayName, Description: p.Description)),
            "show" => (response.Shows?.Items ?? []).Where(s => s != null).Select(s => new SpotifySearchItem(s.Uri, type, s.Name, Description: s.Description)),
            _ => (response.Episodes?.Items ?? []).Where(e => e != null).Select(e => new SpotifySearchItem(e.Uri, type, e.Name, ReleaseDate: e.ReleaseDate, ReleaseDatePrecision: e.ReleaseDatePrecision, Description: e.Description)),
        };
        return new(JsonSerializer.Serialize(new { query, type, results = results.Where(item => !string.IsNullOrWhiteSpace(item.Uri) && !string.IsNullOrWhiteSpace(item.Name)).Take(10).Select(_selections.Remember).ToArray() }, SearchJson), false);
    }

    private async Task<ChatToolCallResult> Play(string uri, string type, CancellationToken token)
    {
        var result = await manager.PlayWithResult(uri, token, type);
        token.ThrowIfCancellationRequested();
        var item = result.Playback?.Item switch
        {
            FullTrack track => TrackName(track), FullEpisode episode => episode.Name, _ => null,
        };
        return new(JsonSerializer.Serialize(new
        {
            status = result.Confirmed ? "confirmed" : result.Accepted ? "accepted_unconfirmed" : "failed",
            requested_uri = uri,
            observed_item = item,
            observed_uri = result.Playback?.Item switch { FullTrack t => t.Uri, FullEpisode e => e.Uri, _ => null },
            observed_context_uri = result.Playback?.Context?.Uri,
            observed_is_playing = result.Playback?.IsPlaying,
            device = result.Playback?.Device?.Name,
            detail = result.Error ?? (result.Confirmed ? "Requested playback was observed."
                : result.Accepted ? "Spotify accepted the request; playback of the requested item has not been confirmed."
                : "Spotify did not accept the play request."),
        }), !result.Accepted);
    }

    private async Task<ChatToolCallResult> DescribeDevices(CancellationToken token)
    {
        var devices = await manager.GetAvailablePlaybackDevices(token);
        if (manager.LastUserVisibleError != null) return Failure("Could not list devices.");
        return new(devices.Count == 0
            ? "No Spotify Connect devices are available for transfer. The user needs to open Spotify on the intended destination. If playback control reports no active player, the user needs to select music and start playback manually."
            : "Available Spotify Connect devices (active means selected as the player, not necessarily playing): " + FormatDevices(devices), false);
    }

    private static string FormatDevices(IEnumerable<Device> devices) => string.Join("; ", devices.Select(d =>
        $"{d.Name} [type: {d.Type}, active: {d.IsActive.ToString().ToLowerInvariant()}, controllable: {(!d.IsRestricted && !string.IsNullOrWhiteSpace(d.Id)).ToString().ToLowerInvariant()}]"));

    private ChatToolCallResult Failure(string fallback) => new(manager.LastUserVisibleError ?? fallback, true);
    private Task<ChatToolCallResult> Changed(bool success, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(success ? new ChatToolCallResult(text, false)
            : Failure("Spotify did not complete the requested change. Check authorization and the active player."));
    }
    private static string TrackName(FullTrack track) => track.Artists is { Count: > 0 }
        ? $"{track.Name} by {string.Join(", ", track.Artists.Select(a => a.Name))}" : track.Name ?? "Unknown track";
    private static string Text(JsonElement? args, string key)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()))
            throw new ModuleToolInputException($"'{key}' must be a nonempty string.");
        return field.GetString()!.Trim();
    }
    private static string Choice(JsonElement? args, string key, params string[] choices)
    {
        var value = Text(args, key).ToLowerInvariant();
        if (!choices.Contains(value)) throw new ModuleToolInputException($"'{key}' must be one of: {string.Join(", ", choices)}.");
        return value;
    }
    private static int Number(JsonElement? args, int? fallback, int max)
    {
        if (args is { } value && value.TryGetProperty("value", out var field))
        {
            if (field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) && number >= 0 && number <= max) return number;
            throw new ModuleToolInputException($"'value' must be an integer between 0 and {max}.");
        }
        return fallback ?? throw new ModuleToolInputException("'value' is required for this operation.");
    }
}
