using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Abstractions.Authorization;
using Voxta.Abstractions.Encryption;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Utils;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.Spotify.Clients.Models;
using Voxta.Modules.Aios.Spotify.Clients.Services;
using Voxta.Modules.Aios.Spotify.Configuration;

internal static class SpotifyVoxtaOAuthChecks
{
    public static async Task RunAsync()
    {
        var encryption = DispatchProxy.Create<ILocalEncryptionProvider, OAuthEncryptionProxy>();
        var exchange = new FakeExchange();
        var provider = new SpotifyModuleAuthorizationProvider(encryption, exchange);
        var settings = new StaticSettingsSource(new()
        {
            [ModuleConfigurationProvider.ClientId.Name] = "test-client",
            [ModuleConfigurationProvider.ClientSecret.Name] = "encrypted:test-secret",
            [ModuleConfigurationProvider.VoxtaRedirectUri.Name] = SpotifyRedirectUri.VoxtaDefault,
        });
        var field = ModuleConfigurationProvider.SpotifyAccount.Name;
        var url = await provider.GetOAuth2AuthorizationUrlAsync(null!, settings, field, "state-from-voxta", SpotifyRedirectUri.VoxtaDefault, default);
        var parsed = new Uri(url);
        var query = QueryHelpers.ParseQuery(parsed.Query);
        Check(parsed.Host == "accounts.spotify.com" && query["response_type"] == "code", "Wrong authorization flow");
        Check(query["state"] == "state-from-voxta" && query["redirect_uri"] == SpotifyRedirectUri.VoxtaDefault, "Host state or redirect lost");
        Check(query["scope"].ToString().Split(' ').ToHashSet().SetEquals(SpotifyModuleAuthorizationProvider.ScopesRequired), "Scopes differ from fallback");
        foreach (var returnUrl in new[] { "http://127.0.0.1:5385/auth/modules/callback", "http://localhost:5384/auth/modules/callback", "https://example.com/auth/modules/callback" })
            await Throws<InvalidOperationException>(() => provider.GetOAuth2AuthorizationUrlAsync(null!, settings, field, "state", returnUrl, default));
        await Throws<InvalidOperationException>(() => provider.GetOAuth2AuthorizationUrlAsync(null!, settings, "WrongField", "state", SpotifyRedirectUri.VoxtaDefault, default));
        using var cancellation = new CancellationTokenSource();
        var result = await provider.ExchangeCodeForTokenAsync(null!, settings, field, "test-code", cancellation.Token);
        Check(result.EncryptedAccessToken == "plain-access" && exchange.Secret == "test-secret", "Provider must return plaintext for host-owned encryption");
        Check(exchange.Redirect == new Uri(SpotifyRedirectUri.VoxtaDefault) && exchange.Code == "test-code" && exchange.Token == cancellation.Token, "Code exchange inputs lost");
        var desktopSettings = new StaticSettingsSource(new()
        {
            [ModuleConfigurationProvider.ClientId.Name] = "test-client",
            [ModuleConfigurationProvider.ClientSecret.Name] = "encrypted:test-secret",
            [ModuleConfigurationProvider.VoxtaRedirectUri.Name] = SpotifyRedirectUri.DesktopDefault,
        });
        var desktopUrl = await provider.GetOAuth2AuthorizationUrlAsync(null!, desktopSettings, field, "desktop-state", SpotifyRedirectUri.DesktopDefault, default);
        var desktopQuery = QueryHelpers.ParseQuery(new Uri(desktopUrl).Query);
        Check(desktopQuery["redirect_uri"] == SpotifyRedirectUri.DesktopDefault && desktopQuery["state"] == "desktop-state", "Desktop callback or state lost");
        await provider.ExchangeCodeForTokenAsync(null!, desktopSettings, field, "desktop-code", cancellation.Token);
        Check(exchange.Redirect == new Uri(SpotifyRedirectUri.DesktopDefault) && exchange.Code == "desktop-code", "Desktop exchange used browser redirect");
        await Throws<InvalidOperationException>(() => provider.GetOAuth2AuthorizationUrlAsync(null!, settings, field, "state", SpotifyRedirectUri.DesktopDefault, default));
        await Throws<InvalidOperationException>(() => provider.GetOAuth2AuthorizationUrlAsync(null!, desktopSettings, field, "state", SpotifyRedirectUri.VoxtaDefault, default));
        var refreshed = await provider.RefreshTokenAsync(null!, settings, field, "plain-refresh", cancellation.Token);
        Check(refreshed?.EncryptedRefreshToken == "plain-refresh", "Refresh token lost");
        Check((await provider.GetAuthorizationStatusAsync(null!, settings, field, result, default)).Status == AuthorizationFlowStatus.Authorized, "Native account not authorized");
        Check((await provider.GetAuthorizationStatusAsync(null!, settings, field, null, default)).Status == AuthorizationFlowStatus.Undefined, "Missing account accepted");
        cancellation.Cancel();
        await Throws<OperationCanceledException>(() => provider.ExchangeCodeForTokenAsync(null!, settings, field, "code", cancellation.Token));
        Check(exchange.ExchangeCalls == 2, "Canceled code exchange ran");

        var expired = new AccessTokenInfo("expired-access", DateTimeOffset.UtcNow.AddDays(-1), "Spotify", "expired-refresh");
        // The provider must let Voxta clear the account even if access/refresh tokens are expired.
        await provider.SignOutAsync(null!, desktopSettings, field, default);
        await ((IOAuth2ModuleAuthorizationProvider)provider).RevokeTokenAsync(null!, desktopSettings, field, expired.EncryptedRefreshToken!);
        await provider.SignOutAsync(null!, desktopSettings, field, default);
        Check(exchange.ExchangeCalls == 2, "Sign-out attempted OAuth code exchange");
        Check((await provider.GetAuthorizationStatusAsync(null!, desktopSettings, field, null, default)).Status == AuthorizationFlowStatus.Undefined, "Signed-out account still authorized");
        await Throws<OperationCanceledException>(() => provider.SignOutAsync(null!, desktopSettings, field, cancellation.Token));

        foreach (var uri in new[] { SpotifyRedirectUri.VoxtaDefault, SpotifyRedirectUri.DesktopDefault, "https://voxta.example/auth/modules/callback", "https://voxta.example/oauth-native-bounce.html" })
            Check(SpotifyRedirectUri.GetVoxtaError(new Uri(uri)) == null, "Valid host callback rejected");
        foreach (var uri in new[] { "http://localhost:5384/auth/modules/callback", "http://voxta.example/auth/modules/callback", "http://127.0.0.1:5384/extensions/spotify/oauth2/callback", "http://127.0.0.1:5384/auth/modules/callback." })
            Check(SpotifyRedirectUri.GetVoxtaError(new Uri(uri)) != null, "Invalid host callback accepted");
        Check(new StaticSettingsSource(new()).GetRequired(ModuleConfigurationProvider.AuthorizationMode) == ModuleConfigurationProvider.ListenerAuthorization, "Existing fallback default changed");
        Check(new StaticSettingsSource(new()).GetRequired(ModuleConfigurationProvider.VoxtaRedirectUri) == SpotifyRedirectUri.DesktopDefault, "New desktop configuration defaults to browser callback");
        Check(ModuleConfigurationProvider.AuthorizationMode.Choices!.Single(c => c.Value == "voxta").Label == "Spotify OAuth", "Spotify OAuth label or stored mode compatibility changed");
        Check(ModuleConfigurationProvider.UseVoxtaAuthorization(new StaticSettingsSource(new() { ["AuthorizationMode"] = "voxta" })), "Native mode selection lost");
        await Throws<InvalidOperationException>(() =>
        {
            ModuleConfigurationProvider.UseVoxtaAuthorization(new StaticSettingsSource(new() { ["AuthorizationMode"] = "unknown" }));
            return Task.CompletedTask;
        });

        var backend = new FakeHostTokenStore();
        var user = Guid.NewGuid();
        var id = Guid.NewGuid();
        backend.Value = new("encrypted:host-access", DateTimeOffset.UtcNow.AddHours(1), "Spotify user", "encrypted:host-refresh", "scope");
        var storage = new SpotifyVoxtaTokenStore(backend, encryption, user, id);
        var token = await storage.LoadAsync(default);
        Check(token?.AccessToken == "host-access" && token.RefreshToken == "host-refresh", "Host token decryption");
        Check(backend.User == user && backend.Id == id, "Token lookup not user/token scoped");
        await storage.SaveAsync(new() { AccessToken = "new-access", RefreshToken = "new-refresh", ExpiresAt = DateTime.UtcNow.AddHours(1) }, default);
        Check(backend.Value!.EncryptedAccessToken == "encrypted:new-access" && backend.Value.EncryptedRefreshToken == "encrypted:new-refresh", "Refreshed tokens not encrypted");
        Check(backend.Value.Scope == "scope" && backend.Value.Name == "Spotify user", "Host token metadata lost");
        await storage.DeleteAsync(default);
        Check(backend.Value == null && await storage.LoadAsync(default) == null, "Native sign-out left credentials");
        await Throws<InvalidOperationException>(() => storage.SaveAsync(new() { AccessToken = "access" }, default));

        // A usable fallback token must never make the native test succeed.
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var fallback = JsonSerializer.Serialize(new SpotifyAuthToken { AccessToken = "fallback-access", ExpiresAt = DateTime.UtcNow.AddHours(1) });
            await File.WriteAllTextAsync(path, fallback);
            var config = new SpotifyManagerConfig
            {
                UseVoxtaAuthorization = true, UserId = user, VoxtaTokenId = id,
                ClientId = "test-client", ClientSecret = "test-secret", TokenPath = path,
                RedirectUri = new Uri(SpotifyRedirectUri.Default),
            };
            var manager = new SpotifyManager(new ForbiddenCallbacks(), new ForbiddenInteraction(), config,
                NullLogger<SpotifyManager>.Instance, storage);
            await manager.InitializeSpotifyClient(default);
            Check(!manager.HasClient && manager.IsAuthorizationRequired && manager.LastUserVisibleError!.Contains("Spotify Account"), "Native test silently reused fallback credentials");
            Check(await File.ReadAllTextAsync(path) == fallback, "Native mode changed fallback token file");
            backend.Value = new("encrypted:native-access", DateTimeOffset.UtcNow.AddHours(1), "Spotify");
            manager = new SpotifyManager(new ForbiddenCallbacks(), new ForbiddenInteraction(), config,
                NullLogger<SpotifyManager>.Instance, storage);
            await manager.InitializeSpotifyClient(default);
            Check(manager.HasClient && !manager.IsAuthorizationRequired, "Native token not used");
            Check(await File.ReadAllTextAsync(path) == fallback, "Native success touched fallback file");
            await provider.SignOutAsync(null!, desktopSettings, field, default);
            await storage.DeleteAsync(default); // Host sign-out endpoint owns token deletion.
            Check(await manager.GetCurrentPlaybackState(default) == null && manager.IsAuthorizationRequired && !manager.HasClient,
                "An active client survived removal of its stored account token");
            Check(await File.ReadAllTextAsync(path) == fallback, "Native sign-out changed fallback credentials");
        }
        finally { File.Delete(path); }
        Console.WriteLine("PASS Spotify OAuth browser/desktop callbacks, exact exchange URI, expired-token sign-out, client invalidation, token encryption, cancellation and fallback isolation");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class FakeExchange : ISpotifyOAuthTokenClient
    {
        public string? Secret, Code;
        public Uri? Redirect;
        public CancellationToken Token;
        public int ExchangeCalls;
        public Task<AccessTokenInfo> ExchangeAsync(string clientId, string secret, string code, Uri redirect, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ExchangeCalls++; Secret = secret; Code = code; Redirect = redirect; Token = token;
            return Task.FromResult(new AccessTokenInfo("plain-access", DateTimeOffset.UtcNow.AddHours(1), "Spotify", "plain-refresh"));
        }
        public Task<AccessTokenInfo> RefreshAsync(string clientId, string secret, string refreshToken, CancellationToken token) =>
            Task.FromResult(new AccessTokenInfo("refreshed", DateTimeOffset.UtcNow.AddHours(1), "Spotify", refreshToken));
    }

    private sealed class FakeHostTokenStore : IModuleOAuthTokenStore
    {
        public AccessTokenInfo? Value;
        public Guid User, Id;
        public Task<AccessTokenInfo?> GetAsync(Guid user, Guid id, CancellationToken token) { User = user; Id = id; return Task.FromResult(Value); }
        public Task<bool> UpdateAsync(Guid user, Guid id, AccessTokenInfo value, CancellationToken token) { Value = value; return Task.FromResult(true); }
        public Task DeleteAsync(Guid user, Guid id, CancellationToken token) { Value = null; return Task.CompletedTask; }
        public Task<Guid> SaveAsync(Guid user, Guid module, string field, Guid? id, AccessTokenInfo value, CancellationToken token) => throw new Exception("Host owns initial token persistence");
        public Task<IReadOnlyDictionary<string, AccessTokenInfo>> ListByModuleAsync(Guid user, Guid module, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class ForbiddenCallbacks : ISpotifyAuthCallbackManager
    {
        public SpotifyAuthCallbackResult Callback(string code, string state) => throw new Exception("Fallback callback used");
        public SpotifyAuthCallbackResult Reject(string state) => throw new Exception("Fallback callback used");
        public Task<string> WaitForCodeAsync(string state, CancellationToken token) => throw new Exception("Fallback listener path used");
        public void Release(string state) => throw new Exception("Fallback callback used");
    }
    private sealed class ForbiddenInteraction : ISpotifyUserInteractionWrapper
    {
        public CancellationToken Abort => default;
        public Task<IUserInteractionRequestToken> RequestUserInteraction(Uri uri, bool reauthorize, CancellationToken token) => throw new Exception("Fallback authorization popup used");
    }
}

public class OAuthEncryptionProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "Encrypt" => "encrypted:" + (string)args![0]!,
        "Decrypt" => ((string)args![0]!).StartsWith("encrypted:") ? ((string)args[0]!)[10..] : throw new Exception("Expected encrypted credential"),
        _ => throw new NotSupportedException(method.Name),
    };
}
