using System.Net;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;
using Voxta.Abstractions.Authorization;
using Voxta.Abstractions.Encryption;
using Voxta.Modules.Aios.Spotify.Clients.Models;
using Voxta.Modules.Aios.Spotify.Configuration;

namespace Voxta.Modules.Aios.Spotify.Clients.Services;

public class SpotifyManagerConfig
{
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public Guid UserId { get; init; }
    public Guid? TokenId { get; init; }
    public CancellationToken Abort { get; init; }
}

public interface ISpotifyManagerFactory
{
    Task<ISpotifyManager> CreateSpotifyManager(SpotifyManagerConfig config, CancellationToken cancellationToken);
}

public class SpotifyManagerFactory(
    IModuleOAuthTokenStore tokens,
    ILocalEncryptionProvider encryption,
    ISpotifyOAuthTokenClient oauth,
    ILoggerFactory loggerFactory) : ISpotifyManagerFactory
{
    public async Task<ISpotifyManager> CreateSpotifyManager(SpotifyManagerConfig config, CancellationToken cancellationToken)
    {
        var manager = new SpotifyManager(config,
            new SpotifyVoxtaTokenStore(tokens, encryption, config.UserId, config.TokenId), oauth,
            loggerFactory.CreateLogger<SpotifyManager>());
        await manager.InitializeSpotifyClient(cancellationToken);
        return manager;
    }
}

public sealed record SpotifyPlaybackResult(bool Accepted, bool Confirmed, CurrentlyPlayingContext? Playback, string? Error);

public interface ISpotifyManager
{
    string? LastUserVisibleError { get; }
    bool IsAuthorizationRequired { get; }
    Task<CurrentlyPlayingContext?> GetCurrentPlaybackState(CancellationToken cancellationToken);
    Task<Paging<FullTrack>?> GetUsersTopTracks(CancellationToken cancellationToken);
    Task<bool> ControlSpotifyPlayback(bool playback, CancellationToken cancellationToken);
    Task<bool> PlaySpecificUri(string uri, CancellationToken cancellationToken, string? type = null);
    Task<SpotifyPlaybackResult> PlayWithResult(string uri, CancellationToken cancellationToken, string? type = null);
    Task<bool> QueueTrack(string uri, CancellationToken cancellationToken);
    Task<SearchResponse?> SearchSpotify(string query, SearchRequest.Types type, string? market = null, CancellationToken cancellationToken = default);
    Task<string?> GetSpotifyUserIdAsync(CancellationToken cancellationToken = default);
    Task<bool> ChangeVolume(int volumePercent, CancellationToken cancellationToken);
    Task<bool> SkipToPreviousOrNextTrack(string skipToPrevious, CancellationToken cancellationToken);
    Task<bool> SeekPlayback(int positionMs, CancellationToken cancellationToken);
    Task<bool> SetShuffle(bool shuffleState, CancellationToken cancellationToken);
    Task<bool> SetRepeatMode(string repeatMode, CancellationToken cancellationToken);
    Task<Dictionary<string, string>> ListAvailablePlaylists(CancellationToken cancellationToken);
    Task<bool> AddItems(string playlistId, PlaylistAddItemsRequest request, CancellationToken cancellationToken);
    Task<bool> AddTrackToLibraryAsync(string trackUri, string trackFriendlyName, CancellationToken cancellationToken);
    Task<Dictionary<string, string>> ListAvailableDevices(CancellationToken cancellationToken);
    Task<IReadOnlyList<Device>> GetAvailablePlaybackDevices(CancellationToken cancellationToken);
    Task<bool> TransferPlayback(string deviceId, CancellationToken cancellationToken);
}
    
public class SpotifyManager(
    SpotifyManagerConfig config,
    ISpotifyTokenStore tokenStore,
    ISpotifyOAuthTokenClient oauth,
    ILogger<SpotifyManager> logger) : ISpotifyManager
{
    private const string AuthorizationRequiredMessage = "Spotify authorization is required. Save your client credentials and sign in using Spotify Account in module settings, then test the module or start a new chat.";
    private const string ReauthorizationRequiredMessage = "Spotify authorization has expired or is no longer valid. Sign out and sign in again using Spotify Account in module settings.";
    private const string AuthorizationFailedMessage = "Spotify could not refresh authorization. Sign out and sign in again using Spotify Account in module settings, then test the module or start a new chat.";
    internal const string NoActiveDeviceMessage = "Spotify has no active player. Please open Spotify on the intended device, select a track or playlist and start playback manually. Playback control can continue once music has started.";
    private readonly SemaphoreSlim _authorizationGate = new(1, 1);
    private SpotifyClient? _spotifyClient;
    private SpotifyAPI.Web.Http.IAPIConnector? _apiConnector;
    private string? _clientAccessToken;
    private SpotifyAuthToken? _spotifyAuthToken;
    private bool _forceTokenRefresh;
    private bool _reauthorizationRequired;
    private bool _authorizationRetrySuppressed;
    private bool _authorizationUnavailableLogged;
    public string? LastUserVisibleError { get; private set; }
    public bool IsAuthorizationRequired { get; private set; }
    public bool HasClient => _spotifyClient != null;

    public async Task InitializeSpotifyClient(CancellationToken cancellationToken)
    {
        var accessToken = await GetValidAccessTokenAsync(cancellationToken);
        if (accessToken != null)
        {
            ConfigureClient(SpotifyClientConfig.CreateDefault().WithToken(accessToken));
            _clientAccessToken = accessToken;
            logger.LogInformation("Spotify client initialized using the account stored by Voxta.");
        }
    }

    private async Task<string?> GetValidAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _authorizationGate.WaitAsync(cancellationToken);
        try
        {
            var token = await tokenStore.LoadAsync(cancellationToken);
            _spotifyAuthToken = token;
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                SetAuthorizationRequired();
                return null;
            }
            if (DateTime.UtcNow >= token.ExpiresAt || _forceTokenRefresh)
            {
                _reauthorizationRequired = true;
                if (_authorizationRetrySuppressed || string.IsNullOrWhiteSpace(token.RefreshToken))
                {
                    SetAuthorizationRequired();
                    return null;
                }
                try
                {
                    var refreshed = await oauth.RefreshAsync(config.ClientId, config.ClientSecret, token.RefreshToken, cancellationToken);
                    token = new SpotifyAuthToken
                    {
                        AccessToken = refreshed.EncryptedAccessToken,
                        RefreshToken = refreshed.EncryptedRefreshToken ?? token.RefreshToken,
                        ExpiresAt = refreshed.Expires.UtcDateTime,
                    };
                    await tokenStore.SaveAsync(token, cancellationToken);
                    _spotifyAuthToken = token;
                    _forceTokenRefresh = false;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _authorizationRetrySuppressed = true;
                    logger.LogWarning(ex, "Spotify authorization refresh failed.");
                    if (IsInvalidGrant(ex))
                    {
                        await tokenStore.DeleteAsync(cancellationToken);
                        _spotifyAuthToken = null;
                    }
                    SetAuthorizationRequired();
                    return null;
                }
            }
            _authorizationRetrySuppressed = false;
            _reauthorizationRequired = false;
            ClearUserVisibleError();
            return token.AccessToken;
        }
        finally { _authorizationGate.Release(); }
    }

    private static bool IsInvalidGrant(Exception ex)
    {
        return ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CurrentlyPlayingContext?> GetCurrentPlaybackState(CancellationToken cancellationToken)
    {
        try
        {
            if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
            {
                logger.LogDebug("Spotify client is not authorized. Playback state was not polled.");
                return null;
            }

            var state = await _spotifyClient.Player.GetCurrentPlayback(cancellationToken);
            ClearUserVisibleError();
            return state;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "retrieve playback state");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError("Error retrieving playback state: {ExMessage}", ex.Message);
            LastUserVisibleError = "Spotify playback state could not be retrieved. Please check Spotify and try again.";
            return null;
        }
    }

    public async Task<Paging<FullTrack>?> GetUsersTopTracks(CancellationToken cancellationToken)
    {
        try
        {
            if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
            {
                logger.LogError("Spotify client not valid. Playback state cannot be retrieved.");
                return null;
            }

            return await _spotifyClient.Personalization.GetTopTracks(cancellationToken);
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "retrieve top tracks");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError("Error retrieving top tracks: {ExMessage}", ex.Message);
            LastUserVisibleError = "Spotify top tracks could not be retrieved. Please check Spotify and try again.";
            return null;
        }
    }

    public async Task<bool> ControlSpotifyPlayback(bool playback, CancellationToken cancellationToken)
    {
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Playback cannot be controlled.");
            return false;
        }

        try
        {
            if (playback)
            {
                await _spotifyClient.Player.ResumePlayback(cancellationToken);
                logger.LogInformation("Playback resumed.");
            }
            else
            {
                await _spotifyClient.Player.PausePlayback(cancellationToken);
                logger.LogInformation("Playback paused.");
            }
            ClearUserVisibleError();
            return true;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "control playback");
            return false;
        }
    }

    public async Task<bool> PlaySpecificUri(string uri, CancellationToken cancellationToken, string? type = null)
    {
        var result = await StartPlayback(uri, cancellationToken, type, false);
        return result.Confirmed;
    }

    public Task<SpotifyPlaybackResult> PlayWithResult(string uri, CancellationToken cancellationToken, string? type = null) =>
        StartPlayback(uri, cancellationToken, type, true);

    private async Task<SpotifyPlaybackResult> StartPlayback(string uri, CancellationToken cancellationToken, string? type, bool forTool)
    {
        var accepted = false;
        CurrentlyPlayingContext? observed = null;
        try
        {
            if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
            {
                logger.LogError("Spotify client not valid. Cannot play specific URI.");
                return new(false, false, null, LastUserVisibleError);
            }

            var request = new PlayerResumePlaybackRequest();
            var expectedUri = uri;

            if (type == "track" || uri.StartsWith("spotify:track:", StringComparison.Ordinal))
            {
                // TEMPORARY WORKAROUND: desktop clients can accept bare track URIs but clear playback.
                // Report: https://community.spotify.com/forum/en/discussion/7552922/web-api-me-player-play-returns-204-but-does-not-start-playback-and-clears-current-playback-desktop
                // TODO: Remove this album lookup/context/offset path once direct single-track `uris`
                // requests reliably start the requested song on affected desktop clients, from both
                // playing and paused states, without clearing the player. Verify against live Spotify;
                // a 204 response or mocked tests alone do not establish that the upstream bug is fixed.
                // Restore single-track playback so later album tracks are not added by this workaround.
                // Keep the playback-state confirmation below after removal; never retry a play command.
                var track = await _spotifyClient.Tracks.Get(uri.Split(':').Last(), cancellationToken);
                if (string.IsNullOrWhiteSpace(track.Album?.Uri) || string.IsNullOrWhiteSpace(track.Uri))
                {
                    LastUserVisibleError = "Spotify could not find the track's album. Playback was not changed.";
                    return new(false, false, null, LastUserVisibleError);
                }
                request.ContextUri = track.Album.Uri;
                expectedUri = track.Uri;
                request.OffsetParam = new PlayerResumePlaybackRequest.Offset { Uri = track.Uri };
                request.PositionMs = 0;
            }
            else if (type == "episode")
                request.Uris = new List<string> { uri };
            else
                request.ContextUri = uri;

            await _spotifyClient.Player.ResumePlayback(request, cancellationToken);
            accepted = true;
            // A successful HTTP response only acknowledges the command. Allow Connect time to settle.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) await Task.Delay(400, cancellationToken);
                var state = await GetCurrentPlaybackState(cancellationToken);
                observed = state;
                if (LastUserVisibleError != null) return new(true, false, observed, LastUserVisibleError);
                var matches = state?.Item switch
                {
                    FullTrack current => current.Uri == expectedUri,
                    FullEpisode current => current.Uri == expectedUri,
                    _ => false,
                };
                var artistMatches = forTool && uri.StartsWith("spotify:artist:", StringComparison.Ordinal)
                    && state?.Item is FullTrack artistTrack && artistTrack.Artists?.Any(a => a.Uri == uri) == true;
                // For exact tracks/episodes a matching context alone does not prove the item started.
                var contextMatches = state?.Context?.Uri == uri && (!forTool || type is not ("track" or "episode"));
                if (state?.IsPlaying == true && (matches || contextMatches || artistMatches))
                {
                    ClearUserVisibleError();
                    return new(true, true, state, null);
                }
            }
            logger.LogWarning("Spotify accepted playback for {Uri}, but the requested item was not observed playing.", uri);
            if (!forTool)
                LastUserVisibleError = "Spotify accepted the play request, but playback of the requested item could not be confirmed. Check Spotify on the active device and start a track manually if the player is empty.";
            return new(true, false, observed, null);
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "start playback");
            return new(accepted, false, observed, LastUserVisibleError);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError("Error starting playback: {ExMessage}", ex.Message);
            LastUserVisibleError = forTool && accepted
                ? "Spotify accepted the play request, but playback state could not be read."
                : "Spotify could not start playback. Please check the selected item and active device, then try again.";
            return new(accepted, false, observed, LastUserVisibleError);
        }
    }

    public async Task<bool> QueueTrack(string uri, CancellationToken cancellationToken)
    {
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot queue track.");
            return false;
        }

        try
        {
            await _spotifyClient.Player.AddToQueue(new PlayerAddToQueueRequest(uri), cancellationToken);
            ClearUserVisibleError();
            return true;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "queue track");
            return false;
        }
    }

    public async Task<SearchResponse?> SearchSpotify(string query, SearchRequest.Types type, string? market = null, CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, config.Abort);
        cancellationToken = linkedCancellation.Token;
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot search Spotify.");
            return null;
        }

        var request = new SearchRequest(type, query)
        {
            Market = market,
            Limit = 10
        };

        try
        {
            var response = await _spotifyClient.Search.Item(request, cancellationToken);
            ClearUserVisibleError();
            return response;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "search Spotify");
            return null;
        }
    }

    public async Task<string?> GetSpotifyUserIdAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, config.Abort);
        cancellationToken = linkedCancellation.Token;
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            SetAuthorizationRequired();
            return null;
        }

        try
        {
            var me = await _spotifyClient.UserProfile.Current(cancellationToken);
            logger.LogInformation("Retrieved Spotify user ID: {MeId}", me.Id);
            return me.Id;
        }
        catch (APIException ex)
        {
            logger.LogError(ex, "Failed to retrieve Spotify user ID.");
            SetSpotifyApiError(ex, "retrieve Spotify user profile");
            return null;
        }
    }

    public async Task<bool> ChangeVolume(int volumePercent, CancellationToken cancellationToken)
    {
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot change volume.");
            return false;
        }

        try
        {
            await _spotifyClient.Player.SetVolume(new PlayerVolumeRequest(volumePercent), cancellationToken);
            logger.LogInformation("Volume set to {VolumePercent}%", volumePercent);
            ClearUserVisibleError();
            return true;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "change volume");
            return false;
        }
    }

    public async Task<bool> SkipToPreviousOrNextTrack(string skipToPrevious, CancellationToken cancellationToken)
    {
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot skip track.");
            return false;
        }

        try
        {
            if (skipToPrevious == "previous")
            {
                await _spotifyClient.Player.SkipPrevious(cancellationToken);
                logger.LogInformation("Skipped to previous track");
            }
            else
            {
                await _spotifyClient.Player.SkipNext(cancellationToken);
                logger.LogInformation("Skipped to next track");
            }
            ClearUserVisibleError();
            return true;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "skip track");
            return false;
        }
    }

    public async Task<bool> SeekPlayback(int positionMs, CancellationToken cancellationToken)
    {
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot seek playback.");
            return false;
        }

        try
        {
            await _spotifyClient.Player.SeekTo(new PlayerSeekToRequest(positionMs), cancellationToken);
            logger.LogInformation("Playback position set to {PositionMs} ms.", positionMs);
            ClearUserVisibleError();
            return true;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "seek playback");
            return false;
        }
    }

    public async Task<bool> SetShuffle(bool shuffleState, CancellationToken cancellationToken)
    {
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot set shuffle mode.");
            return false;
        }

        try
        {
            await _spotifyClient.Player.SetShuffle(new PlayerShuffleRequest(shuffleState), cancellationToken);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) await Task.Delay(400, cancellationToken);
                var state = await GetCurrentPlaybackState(cancellationToken);
                if (LastUserVisibleError != null) return false;
                if (state?.Device != null && state.ShuffleState == shuffleState)
                {
                    logger.LogInformation("Shuffle mode confirmed {Mode}", shuffleState ? "on" : "off");
                    return true;
                }
            }
            LastUserVisibleError = $"Spotify accepted the shuffle request, but shuffle {(shuffleState ? "on" : "off")} could not be confirmed on the active player. Do not report shuffle as changed or automatically retry. Check Spotify; the current playback context may not support shuffle.";
            return false;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "set shuffle mode");
            return false;
        }
    }

    public async Task<bool> SetRepeatMode(string repeatMode, CancellationToken cancellationToken)
    {
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot set repeat mode.");
            return false;
        }
        PlayerSetRepeatRequest.State repeatState;

        switch (repeatMode.ToLower())
        {
            case "track":
                repeatState = PlayerSetRepeatRequest.State.Track;
                break;
            case "context":
                repeatState = PlayerSetRepeatRequest.State.Context;
                break;
            case "off":
                repeatState = PlayerSetRepeatRequest.State.Off;
                break;
            default:
                logger.LogError("Invalid repeat mode: {RepeatMode}", repeatMode);
                LastUserVisibleError = "Invalid Spotify repeat mode. Please use track, context, or off.";
                return false;
        }

        try
        {
            var request = new PlayerSetRepeatRequest(repeatState);
            var result = await _spotifyClient.Player.SetRepeat(request, cancellationToken);
            if (result)
            {
                logger.LogInformation("Repeat mode set to {RepeatMode}", repeatMode);
                ClearUserVisibleError();
                return true;
            }

            logger.LogError("Failed to set repeat mode to {RepeatMode}.", repeatMode);
            LastUserVisibleError = "Spotify did not accept the repeat mode change. Please check the active device and try again.";
            return false;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "set repeat mode");
            return false;
        }
    }

    public async Task<Dictionary<string, string>> ListAvailablePlaylists(CancellationToken cancellationToken)
    {
        var playlistMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot list playlists.");
            return playlistMap;
        }

        try
        {
            var page = await _spotifyClient.Playlists.CurrentUsers(cancellationToken);

            while (page.Items is { Count: > 0 })
            {
                foreach (var playlist in page.Items)
                {
                    if (string.IsNullOrEmpty(playlist.Name) || string.IsNullOrEmpty(playlist.Uri))
                        continue;

                    playlistMap[playlist.Name] = playlist.Uri;
                }

                if (string.IsNullOrEmpty(page.Next))
                    break;

                page = await _spotifyClient.NextPage(page).WaitAsync(cancellationToken);
            }
            ClearUserVisibleError();
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "list playlists");
        }

        return playlistMap;
    }

    public async Task<bool> AddItems(string playlistId, PlaylistAddItemsRequest request, CancellationToken cancellationToken)
    {
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot add items to playlist.");
            return false;
        }

        try
        {
            await _spotifyClient.Playlists.AddPlaylistItems(playlistId, request, cancellationToken);
            logger.LogInformation("Track added to playlist: {PlaylistId}", playlistId);
            ClearUserVisibleError();
            return true;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "add track to playlist");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError("Failed to add track to playlist: {ExMessage}", ex.Message);
            LastUserVisibleError = "Spotify could not add the track to the playlist. Please check the playlist and try again.";
            return false;
        }
    }

    public async Task<bool> AddTrackToLibraryAsync(string trackUri, string trackFriendlyName,
        CancellationToken cancellationToken)
    {
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot save track to library.");
            return false;
        }

        try
        {
            // SDK 7.4.2 sends uris in the body; Spotify requires a query parameter.
            // Keep the SDK connector for authorization, cancellation and API error handling.
            await _apiConnector!.Put(new Uri("me/library", UriKind.Relative),
                new Dictionary<string, string> { ["uris"] = trackUri }, null, cancellationToken);

            logger.LogInformation("Track {TrackFriendlyName} added to Liked Songs.", trackFriendlyName);
            ClearUserVisibleError();
            return true;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "save track to Liked Songs");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError("Failed to save track to library: {ExMessage}", ex.Message);
            LastUserVisibleError = "Spotify could not save the track to Liked Songs. Please try again.";
            return false;
        }
    }

    public async Task<Dictionary<string, string>> ListAvailableDevices(CancellationToken cancellationToken)
    {
        var deviceMap = new Dictionary<string, string>();
        foreach (var device in await GetAvailablePlaybackDevices(cancellationToken))
        {
            if (!string.IsNullOrEmpty(device.Name) && !string.IsNullOrEmpty(device.Id))
                deviceMap[device.Name] = device.Id;
        }
        return deviceMap;
    }

    public async Task<IReadOnlyList<Device>> GetAvailablePlaybackDevices(CancellationToken cancellationToken)
    {
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot list available devices.");
            return [];
        }

        try
        {
            var response = await _spotifyClient.Player.GetAvailableDevices(cancellationToken);
            ClearUserVisibleError();
            return response.Devices;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "list available devices");
        }

        return [];
    }

    public async Task<bool> TransferPlayback(string deviceId, CancellationToken cancellationToken)
    {
        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot transfer playback.");
            return false;
        }

        try
        {
            await _spotifyClient.Player.TransferPlayback(new PlayerTransferPlaybackRequest(new List<string> { deviceId }), cancellationToken);
            logger.LogInformation("Playback transferred to device: {DeviceId}", deviceId);
            ClearUserVisibleError();
            return true;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "transfer playback");
            return false;
        }
    }

    private async Task<bool> EnsureValidSpotifyClient(CancellationToken cancellationToken)
    {
        var newToken = await GetValidAccessTokenAsync(cancellationToken);

        if (newToken == null)
        {
            if (!_authorizationUnavailableLogged)
            {
                logger.LogWarning("Spotify is unavailable until authorization is completed.");
                _authorizationUnavailableLogged = true;
            }
            SetAuthorizationRequired();
            return false;
        }

        if (_spotifyAuthToken == null)
        {
            SetAuthorizationRequired();
            return false;
        }

        if (_spotifyClient == null || _clientAccessToken != newToken)
        {
            ConfigureClient(SpotifyClientConfig.CreateDefault().WithToken(newToken));
            _clientAccessToken = newToken;
        }

        _authorizationUnavailableLogged = false;
        return true;
    }

    internal void ConfigureClient(SpotifyClientConfig clientConfig)
    {
        _apiConnector = clientConfig.BuildAPIConnector();
        _spotifyClient = new SpotifyClient(clientConfig.WithAPIConnector(_apiConnector));
    }

    private void ClearUserVisibleError()
    {
        LastUserVisibleError = null;
        IsAuthorizationRequired = false;
    }

    private void SetAuthorizationRequired()
    {
        _spotifyClient = null;
        _clientAccessToken = null;
        IsAuthorizationRequired = true;
        LastUserVisibleError = _authorizationRetrySuppressed ? AuthorizationFailedMessage
            : _reauthorizationRequired ? ReauthorizationRequiredMessage : AuthorizationRequiredMessage;
    }

    private void SetSpotifyApiError(APIException ex, string operation)
    {
        logger.LogWarning(ex, "Spotify API failed while trying to {Operation}.", operation);

        var message = ex.Message;
        if (ex.Response?.StatusCode == HttpStatusCode.Unauthorized ||
            message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid token", StringComparison.OrdinalIgnoreCase))
        {
            _spotifyClient = null;
            _forceTokenRefresh = true;
            _reauthorizationRequired = true;
            SetAuthorizationRequired();
            return;
        }

        if (message.Contains("no active device", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no active player", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("NO_ACTIVE_DEVICE", StringComparison.OrdinalIgnoreCase))
        {
            LastUserVisibleError = NoActiveDeviceMessage;
            return;
        }

        LastUserVisibleError = $"Spotify could not {operation}. Please check Spotify and try again.";
    }

}
