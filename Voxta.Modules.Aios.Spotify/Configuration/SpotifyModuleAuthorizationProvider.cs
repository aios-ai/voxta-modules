using SpotifyAPI.Web;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;
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
    SpotifyOAuthFlows flows, IHttpContextAccessor requestContext,
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
            ? new AuthorizationStatus("Spotify authorization is stored securely by Voxta.", AuthorizationFlowStatus.Authorized, token.Name, token.Expires)
            : new AuthorizationStatus("Register both redirect URLs shown above in your Spotify application, save your client credentials, then sign in here. Desktop or browser is selected automatically.", AuthorizationFlowStatus.Undefined));
    }

    public Task<string> GetOAuth2AuthorizationUrlAsync(IAuthenticationContext auth, StaticSettingsSource config,
        string field, string state, string returnUrl, CancellationToken cancellationToken)
    {
        ValidateField(field);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var redirect))
            throw new InvalidOperationException("Voxta supplied an invalid Spotify OAuth callback address.");
        if (SpotifyRedirectUri.GetError(redirect) is { } redirectError)
            throw new InvalidOperationException(redirectError);
        var request = requestContext.HttpContext?.Request;
        if (request == null || !Uri.TryCreate($"{request.Scheme}://{request.Host}/", UriKind.Absolute, out var origin) ||
            !string.Equals(origin.GetLeftPart(UriPartial.Authority), redirect.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Spotify callback must use the same server origin as the Voxta sign-in request.");
        if (string.IsNullOrWhiteSpace(state)) throw new InvalidOperationException("Voxta did not supply OAuth state. Start authorization again from the Spotify Account field.");
        var credentials = Credentials(config);
        var url = new LoginRequest(redirect, credentials.ClientId, LoginRequest.ResponseType.Code)
            { State = state, Scope = ScopesRequired }.ToUri().AbsoluteUri;
        flows.Begin(auth.UserId, ModuleId(), credentials.Fingerprint, redirect);
        return Task.FromResult(url);
    }

    public Task<AccessTokenInfo> ExchangeCodeForTokenAsync(IAuthenticationContext auth, StaticSettingsSource config,
        string field, string code, CancellationToken cancellationToken)
    {
        ValidateField(field);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Spotify did not return an authorization code.");
        var credentials = Credentials(config);
        var redirect = flows.Consume(auth.UserId, ModuleId(), credentials.Fingerprint);
        return tokens.ExchangeAsync(credentials.ClientId, credentials.Secret, code, redirect, cancellationToken);
    }

    public Task<AccessTokenInfo?> RefreshTokenAsync(IAuthenticationContext auth, StaticSettingsSource config,
        string field, string refreshToken, CancellationToken cancellationToken) => RefreshCoreAsync(config, field, refreshToken, cancellationToken);

    private async Task<AccessTokenInfo?> RefreshCoreAsync(StaticSettingsSource config, string field, string refreshToken, CancellationToken token)
    {
        ValidateField(field);
        return await tokens.RefreshAsync(config.GetRequired(ModuleConfigurationProvider.ClientId),
            encryption.Decrypt(config.GetRequired(ModuleConfigurationProvider.ClientSecret)), refreshToken, token);
    }

    // Voxta owns token deletion. Sign-out also abandons any unfinished sign-in for this module.
    public Task SignOutAsync(IAuthenticationContext auth, StaticSettingsSource config, string field, CancellationToken cancellationToken)
    {
        ValidateField(field);
        cancellationToken.ThrowIfCancellationRequested();
        flows.Cancel(auth.UserId, ModuleId());
        logger?.LogInformation("Spotify OAuth sign-out requested. Voxta will remove the stored account token and clear the account field.");
        return Task.CompletedTask;
    }

    private Guid ModuleId()
    {
        if (Guid.TryParse(requestContext.HttpContext?.Request.RouteValues["moduleId"]?.ToString(), out var moduleId) && moduleId != Guid.Empty)
            return moduleId;
        throw new InvalidOperationException("Spotify sign-in must be started through Voxta's module account UI.");
    }

    private (string ClientId, string Secret, string Fingerprint) Credentials(StaticSettingsSource config)
    {
        var clientId = config.GetRequired(ModuleConfigurationProvider.ClientId);
        var secret = encryption.Decrypt(config.GetRequired(ModuleConfigurationProvider.ClientSecret));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId + "\0" + secret)));
        return (clientId, secret, fingerprint);
    }

    private static void ValidateField(string field)
    {
        if (field != ModuleConfigurationProvider.SpotifyAccount.Name)
            throw new InvalidOperationException("Unknown Spotify authorization field.");
    }
}
