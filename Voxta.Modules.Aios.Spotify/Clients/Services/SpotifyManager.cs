using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Utils;
using Voxta.Modules.Aios.Spotify.Clients.Models;
using Voxta.Modules.Aios.Spotify.Configuration;

namespace Voxta.Modules.Aios.Spotify.Clients.Services;

public interface ISpotifyUserInteractionWrapper
{
    CancellationToken Abort { get; }
    Task<IUserInteractionRequestToken> RequestUserInteraction(Uri url, bool isReauthorization, CancellationToken cancellationToken);
}

public class SpotifyUserInteractionWrapper(IChatSessionChatAugmentationApi session, Guid moduleId) : ISpotifyUserInteractionWrapper
{
    public CancellationToken Abort => session.Chat.Abort;

    public Task<IUserInteractionRequestToken> RequestUserInteraction(Uri url, bool isReauthorization, CancellationToken cancellationToken)
    {
        return session.RequestUserAction(new UserInteractionRequestInput
        {
            ModuleId = moduleId,
            Message = isReauthorization
                ? "Your Spotify authorization has expired or is no longer valid. Please reauthorize Spotify by visiting the following URL in your browser:"
                : "Please authorize the Spotify integration by visiting the following URL in your browser:",
            Url = url.ToString(),
        }, cancellationToken);
    }
}

public class SpotifyManagerConfig
{
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required Uri RedirectUri { get; init; }
    public required string TokenPath { get; init; }
    public TimeSpan AuthorizationTimeout { get; init; } = TimeSpan.FromMinutes(3);
}

public interface ISpotifyManagerFactory
{
    Task<ISpotifyManager> CreateSpotifyManager(ISpotifyUserInteractionWrapper userInteractionWrapper, SpotifyManagerConfig config, CancellationToken cancellationToken);
}
    
public class SpotifyManagerFactory(
    ISpotifyAuthCallbackManager spotifyAuthCallbackManager,
    ILoggerFactory loggerFactory
) : ISpotifyManagerFactory
{
    public async Task<ISpotifyManager> CreateSpotifyManager(ISpotifyUserInteractionWrapper userInteractionWrapper, SpotifyManagerConfig config, CancellationToken cancellationToken)
    {
        var tokenFolder = Path.GetDirectoryName(Path.GetFullPath(config.TokenPath)) ?? throw new InvalidOperationException("Token path is invalid");
        if (!Directory.Exists(tokenFolder))
            Directory.CreateDirectory(tokenFolder);

        var logger = loggerFactory.CreateLogger<SpotifyManager>();
        var spotifyManager = new SpotifyManager(spotifyAuthCallbackManager, userInteractionWrapper, config, logger);
        await spotifyManager.InitializeSpotifyClient(cancellationToken);
            
        if (!spotifyManager.HasClient)
        {
            logger.LogWarning("Spotify client could not be initialized. The chat session will report the authorization problem to the user.");
        }

        return spotifyManager;
    }
}

public interface ISpotifyManager
{
    string? LastUserVisibleError { get; }
    bool IsAuthorizationRequired { get; }
    Task<CurrentlyPlayingContext?> GetCurrentPlaybackState(CancellationToken cancellationToken);
    Task<Paging<FullTrack>?> GetUsersTopTracks(CancellationToken cancellationToken);
    Task<bool> ControlSpotifyPlayback(bool playback, CancellationToken cancellationToken);
    Task<bool> PlaySpecificUri(string uri, CancellationToken cancellationToken, string? type = null);
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
    Task<bool> TransferPlayback(string deviceId, CancellationToken cancellationToken);
}
    
public class SpotifyManager(
    ISpotifyAuthCallbackManager spotifyAuthCallbackManager,
    ISpotifyUserInteractionWrapper userInteractionWrapper,
    SpotifyManagerConfig config,
    ILogger<SpotifyManager> logger) : ISpotifyManager
{
    private const string AuthorizationRequiredMessage = "Spotify authorization is required. Please open Voxta on this machine and authorize Spotify.";
    private const string ReauthorizationRequiredMessage = "Spotify authorization has expired or is no longer valid. Please reauthorize Spotify.";
    private const string AuthorizationDismissedMessage = "Spotify authorization was dismissed. Start a new chat or test the module when you are ready to authorize.";
    private const string AuthorizationFailedMessage = "Spotify authorization could not be completed. Voxta will not ask again during this chat; start a new chat or test the module to try again.";
    private const string NoActiveDeviceMessage = "No active Spotify device was found. Please start Spotify on your browser, desktop, or mobile app, then try again.";
    private SpotifyClient? _spotifyClient;
    private SpotifyAuthToken? _spotifyAuthToken;
    private bool _forceTokenRefresh;
    private bool _reauthorizationRequired;
    private bool _authorizationPromptDismissed;
    private bool _authorizationRetrySuppressed;
    private bool _authorizationUnavailableLogged;
    private string? _configurationError;
    public string? LastUserVisibleError { get; private set; }
    public bool IsAuthorizationRequired { get; private set; }

    public async Task InitializeSpotifyClient(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initialize Spotify Client");
        var accessToken = await GetValidAccessTokenAsync(cancellationToken);
        if (accessToken != null)
        {
            _spotifyClient = new SpotifyClient(accessToken);
            ClearUserVisibleError();
            logger.LogInformation("Spotify client authenticated and created successfully");
            logger.LogWarning("Note: This plugin acts solely as an interface between Voxta and your Spotify player. You must have an active Spotify device or playback session that the plugin can connect to and control. Once connected, you can pause and resume playback freely, until the device becomes inactive for a certain period of time.");
        }
        else
        {
            logger.LogWarning("Spotify client was not initialized because authorization is unavailable.");
            SetAuthorizationRequired();
        }
    }

    public bool HasClient => _spotifyClient != null;

    private async Task<string?> GetValidAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_configurationError != null)
        {
            SetAuthorizationRequired();
            return null;
        }

        var token = await LoadTokenAsync();

        if ((_authorizationPromptDismissed || _authorizationRetrySuppressed) &&
            (token == null || IsTokenExpired(token) || _forceTokenRefresh))
        {
            SetAuthorizationRequired();
            return null;
        }

        if (token == null || IsTokenExpired(token) || _forceTokenRefresh)
        {
            _reauthorizationRequired |= token != null;
            if (token?.RefreshToken != null)
            {
                try
                {
                    token = await RefreshTokenAsync(token.RefreshToken, cancellationToken);
                    await SaveTokenAsync(token);
                    _forceTokenRefresh = false;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsInvalidGrant(ex))
                {
                    logger.LogWarning(ex, "Spotify refresh token is invalid or expired. Discarding stored token and requesting authorization again.");
                    DiscardStoredToken();
                    SetAuthorizationRequired();
                    token = null;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to refresh Spotify token. User authorization is required.");
                    SetAuthorizationRequired();
                    token = null;
                }
            }
            if (config == null)
            {
                throw new InvalidOperationException("_spotifyConfig null.");
            }

            if (token == null)
            {
                if (SpotifyRedirectUri.GetError(config.RedirectUri) is { } redirectError)
                {
                    _configurationError = redirectError;
                    SetAuthorizationRequired();
                    return null;
                }
                var auth = new OAuthClient();
                var loginRequest = new LoginRequest(
                    config.RedirectUri,
                    config.ClientId,
                    LoginRequest.ResponseType.Code
                )
                {
                    State = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    Scope =
                    [
                        Scopes.UserReadPrivate,
                        Scopes.UserReadPlaybackState,
                        Scopes.UserModifyPlaybackState,
                        Scopes.UserTopRead,
                        Scopes.PlaylistReadPrivate,
                        Scopes.PlaylistModifyPrivate,
                        Scopes.PlaylistReadCollaborative,
                        Scopes.UserLibraryModify,
                        Scopes.UserLibraryRead
                    ]

                };

                var authUri = loginRequest.ToUri();
                string code;
                try
                {
                    code = await GetAuthCodeAsync(authUri, loginRequest.State, _reauthorizationRequired, cancellationToken);
                }
                catch (SpotifyAuthorizationDeniedException)
                {
                    _configurationError = "Spotify authorization was denied. Run the module test again when you are ready to allow access.";
                    SetAuthorizationRequired();
                    return null;
                }
                catch (SpotifyAuthorizationSetupException ex)
                {
                    _configurationError = ex.Message;
                    SetAuthorizationRequired();
                    return null;
                }
                catch (SpotifyAuthorizationDismissedException)
                {
                    _authorizationPromptDismissed = true;
                    SetAuthorizationDismissed();
                    return null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Spotify authorization could not be started or another request is already active.");
                    _authorizationRetrySuppressed = true;
                    SetAuthorizationRequired();
                    return null;
                }

                var tokenRequest = new AuthorizationCodeTokenRequest(
                    config.ClientId,
                    config.ClientSecret,
                    code,
                    config.RedirectUri
                );
                AuthorizationCodeTokenResponse response;
                try
                {
                    response = await auth.RequestToken(tokenRequest, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to exchange Spotify authorization code.");
                    _authorizationRetrySuppressed = true;
                    SetAuthorizationRequired();
                    return null;
                }

                token = new SpotifyAuthToken
                {
                    AccessToken = response.AccessToken,
                    RefreshToken = response.RefreshToken,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(response.ExpiresIn)
                };

                await SaveTokenAsync(token);
                _forceTokenRefresh = false;
                _reauthorizationRequired = false;
                _authorizationPromptDismissed = false;
                _authorizationRetrySuppressed = false;
            }
        }
        ClearUserVisibleError();
        return token.AccessToken;
    }

    private async Task<string> GetAuthCodeAsync(Uri authUri, string state, bool isReauthorization, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(config.AuthorizationTimeout);
        try
        {
            var codeTask = spotifyAuthCallbackManager.WaitForCodeAsync(state, timeout.Token);
            SpotifyOAuthListener listener;
            try { listener = await SpotifyOAuthListener.StartAsync(config.RedirectUri, spotifyAuthCallbackManager, timeout.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new SpotifyAuthorizationSetupException("Spotify could not open its local callback port. Close another pending Spotify authorization or choose a free loopback port in both redirect URI settings."); }
            await using var callbackListener = listener;
            await using var userInteractionToken = await userInteractionWrapper.RequestUserInteraction(authUri, isReauthorization, timeout.Token);
            logger.LogInformation("Waiting for Spotify authentication...");
            var completedTask = await Task.WhenAny(userInteractionToken.Task, codeTask);
            if (completedTask == userInteractionToken.Task && !codeTask.IsCompletedSuccessfully)
            {
                await userInteractionToken.Task;
                throw new SpotifyAuthorizationDismissedException();
            }
            return await codeTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpotifyAuthorizationSetupException("Spotify authorization timed out. Check that the redirect URI matches in Voxta and the Spotify Developer Dashboard, then run the module test again.");
        }
        finally { spotifyAuthCallbackManager.Release(state); }
    }

    private bool IsTokenExpired(SpotifyAuthToken token)
    {
        return DateTime.UtcNow >= token.ExpiresAt;
    }

    private async Task SaveTokenAsync(SpotifyAuthToken token)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(token, options);
        await File.WriteAllTextAsync(config.TokenPath, json);
        _spotifyAuthToken = token;
    }

    private async Task<SpotifyAuthToken> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var auth = new OAuthClient();
        var refreshRequest = new AuthorizationCodeRefreshRequest(config.ClientId, config.ClientSecret, refreshToken);

        var response = await auth.RequestToken(refreshRequest, cancellationToken);

        return new SpotifyAuthToken
        {
            AccessToken = response.AccessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddSeconds(response.ExpiresIn)
        };
    }

    private async Task<SpotifyAuthToken?> LoadTokenAsync()
    {
        if (!File.Exists(config.TokenPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(config.TokenPath);
            _spotifyAuthToken = JsonSerializer.Deserialize<SpotifyAuthToken>(json);
            return _spotifyAuthToken;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load Spotify token. User authorization is required.");
            _reauthorizationRequired = true;
            SetAuthorizationRequired();
            return null;
        }
    }

    private void DiscardStoredToken()
    {
        _spotifyAuthToken = null;
        _spotifyClient = null;

        if (!File.Exists(config.TokenPath))
            return;

        try
        {
            File.Delete(config.TokenPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete expired Spotify token file at {TokenPath}.", config.TokenPath);
        }
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

            return await _spotifyClient.Player.GetCurrentPlayback(cancellationToken);
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "retrieve playback state");
            return null;
        }
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
        try
        {
            if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
            {
                logger.LogError("Spotify client not valid. Cannot play specific URI.");
                return false;
            }

            var request = new PlayerResumePlaybackRequest();

            if (type is "track" or "episode")
                request.Uris = new List<string> { uri };
            else
                request.ContextUri = uri;

            await _spotifyClient.Player.ResumePlayback(request, cancellationToken);
            ClearUserVisibleError();
            return true;
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "start playback");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError("Error starting playback: {ExMessage}", ex.Message);
            LastUserVisibleError = "Spotify could not start playback. Please check the selected item and active device, then try again.";
            return false;
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
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, userInteractionWrapper.Abort);
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
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, userInteractionWrapper.Abort);
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
            logger.LogInformation("Shuffle mode set to {Off}", shuffleState ? "on" : "off");
            ClearUserVisibleError();
            return true;
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
            await _spotifyClient.Library.SaveItems(
                new LibrarySaveItemsRequest([trackUri]), cancellationToken);

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

        if (!await EnsureValidSpotifyClient(cancellationToken) || _spotifyClient == null)
        {
            logger.LogError("Spotify client not valid. Cannot list available devices.");
            return deviceMap;
        }

        try
        {
            var response = await _spotifyClient.Player.GetAvailableDevices(cancellationToken);
            if (response.Devices.Count > 0)
            {
                foreach (var device in response.Devices)
                {
                    if (string.IsNullOrEmpty(device.Name) || string.IsNullOrEmpty(device.Id))
                        continue;

                    deviceMap[device.Name] = device.Id;
                }
                ClearUserVisibleError();
            }
            else
            {
                logger.LogWarning("No devices available.");
                LastUserVisibleError = NoActiveDeviceMessage;
            }
        }
        catch (APIException ex)
        {
            SetSpotifyApiError(ex, "list available devices");
        }

        return deviceMap;
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

        if (_spotifyClient == null || _spotifyAuthToken.AccessToken != newToken)
        {
            _spotifyClient = new SpotifyClient(newToken);
        }

        _authorizationUnavailableLogged = false;
        return true;
    }

    private void ClearUserVisibleError()
    {
        LastUserVisibleError = null;
        IsAuthorizationRequired = false;
    }

    private void SetAuthorizationRequired()
    {
        if (_configurationError != null)
        {
            IsAuthorizationRequired = true;
            LastUserVisibleError = _configurationError;
            return;
        }

        if (_authorizationPromptDismissed)
        {
            SetAuthorizationDismissed();
            return;
        }

        if (_authorizationRetrySuppressed)
        {
            IsAuthorizationRequired = true;
            LastUserVisibleError = AuthorizationFailedMessage;
            return;
        }

        IsAuthorizationRequired = true;
        LastUserVisibleError = _reauthorizationRequired
            ? ReauthorizationRequiredMessage
            : AuthorizationRequiredMessage;
    }

    private void SetAuthorizationDismissed()
    {
        IsAuthorizationRequired = true;
        LastUserVisibleError = AuthorizationDismissedMessage;
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

        if (message.Contains("404", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no active device", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("device", StringComparison.OrdinalIgnoreCase))
        {
            LastUserVisibleError = NoActiveDeviceMessage;
            return;
        }

        LastUserVisibleError = $"Spotify could not {operation}. Please check Spotify and try again.";
    }

    private sealed class SpotifyAuthorizationDismissedException : Exception;
    private sealed class SpotifyAuthorizationSetupException(string message) : Exception(message);
}
