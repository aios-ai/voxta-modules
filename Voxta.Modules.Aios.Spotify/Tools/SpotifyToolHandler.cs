using System.Text.Json;
using SpotifyAPI.Web;
using Voxta.Abstractions.Services.Agents;
using Voxta.Modules.Aios.Spotify.Clients.Services;
using Voxta.Modules.Aios.Spotify.Helpers;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.Spotify.Tools;

internal sealed class SpotifyToolHandler(
    ISpotifyManager manager,
    SpotifySearchService search,
    IReadOnlyDictionary<string, string> specialPlaylists,
    SpotifySpeechDucking ducking,
    Func<string, CancellationToken, Task> recordCommand,
    Func<IReadOnlyList<Device>, CancellationToken, Task>? recordDevices = null)
{
    // Serialize stateful search selection and commands; never retry a side effect.
    private readonly SemaphoreSlim _gate = new(1, 1);

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
            case "spotify_connect":
                return await DescribeDevices(token);
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
                return await Changed(await manager.PlaySpecificUri(track.Uri, token, "track"), $"Playing {TrackName(track)}.", token);
            }
            case "play_special_playlist":
            {
                var name = Text(args, "name");
                var match = specialPlaylists.FirstOrDefault(p => StringUtils.NormaliseKey(p.Key) == StringUtils.NormaliseKey(name));
                if (match.Key == null) return new("That special playlist is not configured. Available names: " + string.Join(", ", specialPlaylists.Keys), true);
                return await Changed(await manager.PlaySpecificUri($"spotify:playlist:{match.Value}", token, "playlist"), $"Playing playlist {match.Key}.", token);
            }
            case "play_music":
            case "queue_track":
            {
                var name = Text(args, "name");
                var type = operation == "queue_track" ? "track" : Choice(args, "type", "track", "album", "artist", "playlist", "show", "episode", "genre");
                var result = await search.GetSpotifyUri(name, type == "genre" ? "playlist" : type, type, token);
                token.ThrowIfCancellationRequested();
                if (result.Uri == null || result.Type == null) return Failure("No matching Spotify item was found.");
                if (operation == "queue_track" && result.Type != "track") return new("Only tracks can be queued with this tool.", true);
                var success = operation == "queue_track" ? await manager.QueueTrack(result.Uri, token) : await manager.PlaySpecificUri(result.Uri, token, result.Type);
                return await Changed(success, $"{(operation == "queue_track" ? "Queued" : "Playing")} {result.FriendlyName}.", token);
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
                var target = Choice(args, "target", "forward", "backward", "to_time", "to_percent", "middle");
                var value = target == "middle" ? 0 : Number(args, target is "forward" or "backward" ? 10 : null, target == "to_percent" ? 100 : int.MaxValue);
                var state = await manager.GetCurrentPlaybackState(token);
                var duration = state?.Item switch { FullTrack track => track.DurationMs, FullEpisode episode => episode.DurationMs, _ => 0 };
                if (state == null || duration <= 0) return Failure("No track or episode is currently available to seek.");
                long position = target switch
                {
                    "forward" => state.ProgressMs + (long)value * 1000,
                    "backward" => state.ProgressMs - (long)value * 1000,
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
                if (recordDevices != null) await recordDevices(devices, token);
                var matches = devices.Where(d => !string.IsNullOrWhiteSpace(d.Id) && d.Id.Equals(name, StringComparison.Ordinal)).ToArray();
                if (matches.Length == 0) matches = devices.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 0) matches = devices.Where(d => d.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true).ToArray();
                if (matches.Length != 1) return new(matches.Length == 0 ? "No matching device was found." : "Device name is ambiguous. Specify a device ID from: " + FormatDevices(matches), true);
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
                if (name == null) return await Changed(await manager.AddTrackToLibraryAsync(track.Uri, TrackName(track), token), $"Saved {TrackName(track)} to Liked Songs.", token);
                var playlists = await manager.ListAvailablePlaylists(token);
                if (manager.LastUserVisibleError != null) return Failure("Could not list playlists.");
                var match = playlists.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match.Key == null) return new("That playlist was not found. Use an exact name from the playlist list.", true);
                return await Changed(await manager.AddItems(StringUtils.ExtractIdFromUri(match.Value), new PlaylistAddItemsRequest([track.Uri]), token), $"Added {TrackName(track)} to {match.Key}.", token);
            }
            default: return new("Unsupported Spotify operation.", true);
        }
    }

    private async Task<ChatToolCallResult> DescribeDevices(CancellationToken token)
    {
        var devices = await manager.GetAvailablePlaybackDevices(token);
        if (manager.LastUserVisibleError != null) return Failure("Could not list devices.");
        if (recordDevices != null) await recordDevices(devices, token);
        return new(devices.Count == 0
            ? "No Spotify Connect devices are available. User setup is required: the user opens Spotify on the intended device and confirms it is open before discovery resumes. Brief manual playback is a fallback if it remains unavailable."
            : "Available Spotify Connect devices (active means selected as the player, not necessarily playing): " + FormatDevices(devices), false);
    }

    private static string FormatDevices(IEnumerable<Device> devices) => string.Join("; ", devices.Select(d =>
        $"{d.Name} [type: {d.Type}, active: {d.IsActive.ToString().ToLowerInvariant()}, controllable: {(!d.IsRestricted && !string.IsNullOrWhiteSpace(d.Id)).ToString().ToLowerInvariant()}, id: {d.Id ?? "unavailable"}]"));

    private ChatToolCallResult Failure(string fallback) => new(manager.LastUserVisibleError ?? fallback, true);
    private async Task<ChatToolCallResult> Changed(bool success, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!success) return Failure("Spotify did not complete the requested change. Check authorization and the active player.");
        await recordCommand(text, token);
        return new(text, false);
    }
    private static string TrackName(FullTrack track) => $"{track.Name} by {string.Join(", ", track.Artists.Select(a => a.Name))}";
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
