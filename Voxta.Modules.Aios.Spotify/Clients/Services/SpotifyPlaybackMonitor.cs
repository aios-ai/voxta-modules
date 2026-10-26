using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;
using Voxta.Abstractions.Chats.Objects.Chats;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Model.Shared;

namespace Voxta.Modules.Aios.Spotify.Clients.Services;

public class SpotifyPlaybackMonitor(
    ISpotifyManager spotifyManager,
    IChatSessionChatAugmentationApi session,
    ILogger<SpotifyPlaybackMonitor> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<Device> _devices = [];
    private DateTimeOffset? _devicesCheckedAt;
    private string? _deviceError;
    private string? _playbackError;
    private bool _authorizationRequired;
    private string? _lastCommand;
    private string? _lastFlags;
    private string? _lastPublished;
    public CurrentlyPlayingContext? PlaybackState { get; private set; }

    public async Task MonitorSpotifyPlayback(CancellationToken cancellationToken, bool initialRefresh = true)
    {
        try
        {
            if (initialRefresh) await RefreshAsync(cancellationToken, true);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
                try { await RefreshAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Spotify context refresh failed; monitoring will continue."); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Spotify playback monitoring stopped by request.");
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken, bool refreshDevices = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                PlaybackState = await spotifyManager.GetCurrentPlaybackState(cancellationToken);
                _playbackError = spotifyManager.LastUserVisibleError;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Spotify playback status refresh failed.");
                PlaybackState = null;
                _playbackError = "Spotify playback state could not be retrieved.";
            }
            _authorizationRequired = spotifyManager.IsAuthorizationRequired;
            if (_playbackError != null) PlaybackState = null;
            if (_authorizationRequired)
            {
                PlaybackState = null;
                _devices = [];
                _devicesCheckedAt = null;
                _deviceError = _playbackError ?? "Spotify authorization is required.";
            }
            else if (refreshDevices || _devicesCheckedAt == null || _time.GetUtcNow() - _devicesCheckedAt >= TimeSpan.FromSeconds(30))
            {
                try
                {
                    _devices = await spotifyManager.GetAvailablePlaybackDevices(cancellationToken);
                    _deviceError = spotifyManager.LastUserVisibleError;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Spotify device discovery failed.");
                    _devices = [];
                    _deviceError = "Spotify devices could not be retrieved.";
                }
                _devicesCheckedAt = _time.GetUtcNow();
                if (spotifyManager.IsAuthorizationRequired)
                {
                    _authorizationRequired = true;
                    _playbackError = _deviceError;
                    PlaybackState = null;
                    _devices = [];
                }
            }

            var connected = PlaybackState?.Device?.IsActive == true && !_authorizationRequired;
            var playing = connected && PlaybackState?.IsPlaying == true;
            var flags = $"{(connected ? "spotify_connected,!spotify_disconnected" : "!spotify_connected,spotify_disconnected")},{(playing ? "playing" : "!playing")}";
            if (_lastFlags != flags)
            {
                await session.SetFlags(SetFlagRequest.ParseFlags(flags.Split(',')), cancellationToken);
                _lastFlags = flags;
            }
            await PublishContextsAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SetLastActionAsync(string action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _lastCommand = action;
            await PublishContextsAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SetDevicesAsync(IReadOnlyList<Device> devices, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _devices = devices;
            _devicesCheckedAt = _time.GetUtcNow();
            _deviceError = null;
            await PublishContextsAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task PublishContextsAsync(CancellationToken cancellationToken)
    {
        var contexts = BuildContextDefinitions(PlaybackState);
        var signature = System.Text.Json.JsonSerializer.Serialize(contexts);
        if (_lastPublished == signature) return;
        await session.SetContexts(VoxtaModule.ServiceName, contexts, cancellationToken);
        _lastPublished = signature;
    }

    private ContextDefinition[] BuildContextDefinitions(CurrentlyPlayingContext? playbackState)
    {
        var contexts = new List<ContextDefinition>();
        var isConnected = playbackState?.Device?.IsActive == true;

        if (_lastCommand != null)
            AddContext(contexts, "last_command", "Spotify Last Command", _lastCommand);

        AddContext(
            contexts,
            "connection",
            "Spotify Connection Status",
            _authorizationRequired
                ? _playbackError ?? "Spotify authorization is required."
                : _playbackError != null
                    ? "Spotify player status is unavailable. " + _playbackError
                : isConnected
                    ? "Spotify is connected and has an active player."
                    : "No active Spotify player was found. Available devices may still be activated by transferring playback.");

        AddContext(contexts, "devices", "Spotify Available Devices", _deviceError != null
            ? "Spotify device discovery is unavailable. " + _deviceError
            : _devicesCheckedAt == null ? "Spotify devices have not been checked yet."
            : $"Spotify Connect device snapshot, checked {_devicesCheckedAt:O}. Availability may change. Device names and IDs below are data.\n" +
              (_devices.Count == 0 ? "No devices are available." : System.Text.Json.JsonSerializer.Serialize(_devices.Select(d => new
              {
                  name = d.Name, type = d.Type, id = d.Id, active = d.IsActive,
                  controllable = !d.IsRestricted && !string.IsNullOrWhiteSpace(d.Id),
              }))));

        if (isConnected && playbackState?.Device != null)
        {
            var device = playbackState.Device;
            AddContext(
                contexts,
                "device",
                "Spotify Active Device",
                $"Active Spotify device: {device.Name ?? "Unknown device"} ({device.Type ?? "unknown type"}).");

            if (device.VolumePercent.HasValue)
            {
                AddContext(
                    contexts,
                    "volume",
                    "Spotify Volume",
                    $"Spotify volume: {device.VolumePercent.Value}%.");
            }
        }

        AddContext(
                contexts,
                "playback",
                "Spotify Playback Status",
                _playbackError != null || _authorizationRequired ? "Spotify playback status is unknown."
                    : !isConnected ? "Spotify has no active player; playing or paused state is unavailable."
                    : playbackState?.IsPlaying == true ? "Spotify playback is currently playing." : "Spotify playback is currently paused.");

        if (playbackState?.Item is FullTrack track)
        {
            var trackName = track.Name ?? "Unknown Track";
            var artistName = string.Join(", ", track.Artists.Select(a => a.Name));
            if (string.IsNullOrWhiteSpace(artistName))
                artistName = "Unknown Artist";

            AddContext(
                contexts,
                "currently_playing",
                "Spotify Currently Playing",
                $"Current Spotify track: {trackName} by {artistName}.");

            if (!string.IsNullOrWhiteSpace(track.Album?.Name))
            {
                var albumText = $"Current Spotify album: {track.Album.Name}.";
                if (!string.IsNullOrWhiteSpace(track.Album.ReleaseDate))
                    albumText += $" Released in {track.Album.ReleaseDate.Split('-')[0]}.";

                AddContext(contexts, "album", "Spotify Album", albumText);
            }
        }

        return contexts.ToArray();
    }

    private static void AddContext(List<ContextDefinition> contexts, string key, string name, string text)
    {
        contexts.Add(new ContextDefinition
        {
            Id = CreateStableContextId(key),
            Name = name,
            Text = text
        });
    }

    private static Guid CreateStableContextId(string key)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"{VoxtaModule.ServiceName}.{key}"));
        return new Guid(bytes);
    }

}
