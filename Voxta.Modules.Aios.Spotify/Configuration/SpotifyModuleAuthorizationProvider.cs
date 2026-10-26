using SpotifyAPI.Web;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Encryption;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Model.Shared.Forms;

namespace Voxta.Modules.Aios.Spotify.Configuration;

public interface ISpotifyOAuthTokenClient
{
    Task<AccessTokenInfo> ExchangeAsync(string clientId, string secret, string code, Uri redirect, CancellationToken token);
    Task<AccessTokenInfo> RefreshAsync(string clientId, string secret, string refreshToken, CancellationToken token);
}

internal sealed class SpotifyOAuthTokenClient : ISpotifyOAuthTokenClient
{
    public async Task<AccessTokenInfo> ExchangeAsync(string clientId, string secret, string code, Uri redirect, CancellationToken token)
    {
        var result = await new OAuthClient().RequestToken(new AuthorizationCodeTokenRequest(clientId, secret, code, redirect), token);
        return new(result.AccessToken, DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn), "Spotify", result.RefreshToken, result.Scope);
    }

    public async Task<AccessTokenInfo> RefreshAsync(string clientId, string secret, string refreshToken, CancellationToken token)
    {
        var result = await new OAuthClient().RequestToken(new AuthorizationCodeRefreshRequest(clientId, secret, refreshToken), token);
        return new(result.AccessToken, DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn), "Spotify", result.RefreshToken ?? refreshToken, result.Scope);
    }
}

/// <summary>Participates in Voxta's own account UI, callback page, state validation, and token persistence.</summary>
public sealed class SpotifyModuleAuthorizationProvider(ILocalEncryptionProvider encryption, ISpotifyOAuthTokenClient tokens,
    ILogger<SpotifyModuleAuthorizationProvider>? logger = null)
    : IOAuth2ModuleAuthorizationProvider
{
    internal static readonly string[] ScopesRequired =
    [
        Scopes.UserReadPrivate, Scopes.UserReadPlaybackState, Scopes.UserModifyPlaybackState, Scopes.UserTopRead,
        Scopes.PlaylistReadPrivate, Scopes.PlaylistModifyPrivate, Scopes.PlaylistReadCollaborative,
        Scopes.UserLibraryModify, Scopes.UserLibraryRead,
    ];

    public Task<AuthorizationStatus> GetAuthorizationStatusAsync(IAuthenticationContext auth, StaticSettingsSource config,
        string field, AccessTokenInfo? token, CancellationToken cancellationToken)
    {
        ValidateField(field);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(token != null && (token.Expires > DateTimeOffset.UtcNow || token.EncryptedRefreshToken != null)
            ? new AuthorizationStatus("Spotify authorization is stored by Voxta. The temporary listener is not used in Spotify OAuth mode.", AuthorizationFlowStatus.Authorized, token.Name, token.Expires)
            : new AuthorizationStatus("Authorize here. Use the desktop callback URI for the desktop app or the browser callback URI for a web browser, and register that exact URI in Spotify.", AuthorizationFlowStatus.Undefined));
    }

    public Task<string> GetOAuth2AuthorizationUrlAsync(IAuthenticationContext auth, StaticSettingsSource config,
        string field, string state, string returnUrl, CancellationToken cancellationToken)
    {
        ValidateField(field);
        cancellationToken.ThrowIfCancellationRequested();
        var redirect = Redirect(config);
        if (!string.Equals(redirect.AbsoluteUri, returnUrl, StringComparison.Ordinal))
        {
            if (Uri.TryCreate(returnUrl, UriKind.Absolute, out var requested) && SpotifyRedirectUri.GetVoxtaError(requested) == null)
                throw new InvalidOperationException($"This Voxta client needs {requested.AbsoluteUri}. Set Spotify OAuth Redirect URI to that exact URI, register it in the Spotify Developer Dashboard, save the module settings, then sign in again.");
            throw new InvalidOperationException("Voxta supplied an unsupported Spotify OAuth callback address. Use the desktop app or web browser sign-in button with a supported callback URI.");
        }
        if (string.IsNullOrWhiteSpace(state)) throw new InvalidOperationException("Voxta did not supply OAuth state. Start authorization again from the Spotify Account field.");
        return Task.FromResult(new LoginRequest(redirect, config.GetRequired(ModuleConfigurationProvider.ClientId), LoginRequest.ResponseType.Code)
        { State = state, Scope = ScopesRequired }.ToUri().AbsoluteUri);
    }

    public Task<AccessTokenInfo> ExchangeCodeForTokenAsync(IAuthenticationContext auth, StaticSettingsSource config,
        string field, string code, CancellationToken cancellationToken)
    {
        ValidateField(field);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Spotify did not return an authorization code.");
        return tokens.ExchangeAsync(config.GetRequired(ModuleConfigurationProvider.ClientId),
            encryption.Decrypt(config.GetRequired(ModuleConfigurationProvider.ClientSecret)), code, Redirect(config), cancellationToken);
    }

    public Task<AccessTokenInfo?> RefreshTokenAsync(IAuthenticationContext auth, StaticSettingsSource config,
        string field, string refreshToken, CancellationToken cancellationToken) => RefreshCoreAsync(config, field, refreshToken, cancellationToken);

    private async Task<AccessTokenInfo?> RefreshCoreAsync(StaticSettingsSource config, string field, string refreshToken, CancellationToken token)
    {
        ValidateField(field);
        return await tokens.RefreshAsync(config.GetRequired(ModuleConfigurationProvider.ClientId),
            encryption.Decrypt(config.GetRequired(ModuleConfigurationProvider.ClientSecret)), refreshToken, token);
    }

    // Voxta deletes its own token on sign-out. The fallback token file is deliberately independent.
    public Task SignOutAsync(IAuthenticationContext auth, StaticSettingsSource config, string field, CancellationToken cancellationToken)
    {
        ValidateField(field);
        cancellationToken.ThrowIfCancellationRequested();
        logger?.LogInformation("Spotify OAuth sign-out requested. Voxta will remove the stored account token and clear the account field.");
        return Task.CompletedTask;
    }

    private static Uri Redirect(StaticSettingsSource config)
    {
        var value = config.GetRequired(ModuleConfigurationProvider.VoxtaRedirectUri);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || SpotifyRedirectUri.GetVoxtaError(uri) is { })
            throw new InvalidOperationException($"Set Spotify OAuth Redirect URI to {SpotifyRedirectUri.DesktopDefault} for the desktop app or {SpotifyRedirectUri.VoxtaDefault} for a web browser, using your server's origin, and register that exact URI in Spotify.");
        return uri;
    }

    private static void ValidateField(string field)
    {
        if (field != ModuleConfigurationProvider.SpotifyAccount.Name)
            throw new InvalidOperationException("Unknown Spotify authorization field.");
    }
}
