using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Abstractions.Authorization;
using Voxta.Abstractions.Encryption;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.Spotify.Clients.Models;
using Voxta.Modules.Aios.Spotify.Clients.Services;
using Voxta.Modules.Aios.Spotify.Configuration;

internal static class SpotifyVoxtaOAuthChecks
{
    public static async Task RunAsync()
    {
        var f = new Fixture();
        foreach (var callback in new[] { SpotifyRedirectUri.DesktopDefault, SpotifyRedirectUri.VoxtaDefault })
        {
            var url = await f.Begin(callback);
            var query = QueryHelpers.ParseQuery(new Uri(url).Query);
            Check(query["redirect_uri"] == callback && query["state"] == "state-from-voxta", "SDK callback/state lost");
            Check(query["response_type"] == "code" && query["scope"].ToString().Split(' ').ToHashSet().SetEquals(SpotifyModuleAuthorizationProvider.ScopesRequired), "Flow or scopes changed");
            // Host creates a new transient provider for the token request.
            var result = await f.Provider().ExchangeCodeForTokenAsync(f.Auth, f.Settings, Field, "code", default);
            Check(f.OAuth.Redirect == new Uri(callback) && result.EncryptedAccessToken == "plain-access", "Exchange guessed callback or prematurely encrypted tokens");
            await Throws<InvalidOperationException>(() => f.Exchange());
        }
        Check(f.OAuth.ExchangeCalls == 2, "Code replay executed");
        Check(f.OAuth.Secret == "test-secret", "Client secret not decrypted");
        Console.WriteLine("PASS automatic desktop/browser switching, transient-provider flow retention, exact exchange URI, scopes/state, and replay rejection");

        f = new();
        foreach (var callback in new[]
        {
            "http://localhost:5384/auth/modules/callback", "http://example.com/auth/modules/callback",
            "http://127.0.0.1:5384/extensions/spotify/oauth2/callback", "http://127.0.0.1:5384/auth/modules/callback.",
            "http://127.0.0.1:5384/oauth-native-bounce.html?code=x", "http://127.0.0.1:5385/auth/modules/callback",
            "https://elsewhere.example/auth/modules/callback",
        }) await Throws<InvalidOperationException>(() => f.Begin(callback));
        await Throws<InvalidOperationException>(() => f.Provider().GetOAuth2AuthorizationUrlAsync(f.Auth, f.Settings, "WrongField", "state", SpotifyRedirectUri.DesktopDefault, default));
        f.Request.HttpContext!.Request.Scheme = "https";
        f.Request.HttpContext.Request.Host = new HostString("voxta.example");
        await f.Begin("https://voxta.example/auth/modules/callback");
        await f.Exchange();
        Check(f.OAuth.Redirect == new Uri("https://voxta.example/auth/modules/callback"), "Custom HTTPS origin lost");
        await Throws<InvalidOperationException>(() => f.Provider().GetOAuth2AuthorizationUrlAsync(f.Auth, f.Settings, Field, "", "https://voxta.example/auth/modules/callback", default));
        f.Request.HttpContext!.Request.RouteValues.Clear();
        await Throws<InvalidOperationException>(() => f.Begin("https://voxta.example/auth/modules/callback"));
        Console.WriteLine("PASS callback path, origin, scheme, field, state and request-context validation");

        f = new();
        var firstModule = f.Module;
        var secondModule = Guid.NewGuid();
        await f.Begin(SpotifyRedirectUri.DesktopDefault);
        f.Module = secondModule;
        await f.Begin(SpotifyRedirectUri.VoxtaDefault);
        f.Module = firstModule;
        await f.Exchange();
        Check(f.OAuth.Redirect == new Uri(SpotifyRedirectUri.DesktopDefault), "Module callback isolation failed");
        f.Module = secondModule;
        await f.Exchange();
        Check(f.OAuth.Redirect == new Uri(SpotifyRedirectUri.VoxtaDefault), "Second module flow lost");
        var firstUser = f.Auth;
        await f.Begin(SpotifyRedirectUri.DesktopDefault);
        f.Auth = new(Guid.NewGuid());
        await Throws<InvalidOperationException>(() => f.Exchange());
        await f.Begin(SpotifyRedirectUri.VoxtaDefault);
        await f.Exchange();
        f.Auth = firstUser;
        await f.Exchange();
        Check(f.OAuth.Redirect == new Uri(SpotifyRedirectUri.DesktopDefault), "User callback isolation failed");
        Console.WriteLine("PASS independent module and user sign-ins");

        f = new();
        await f.Begin(SpotifyRedirectUri.DesktopDefault);
        await f.Begin(SpotifyRedirectUri.DesktopDefault); // Restarting in the same client keeps the callback unambiguous.
        await Throws<InvalidOperationException>(() => f.Begin(SpotifyRedirectUri.VoxtaDefault));
        await f.Exchange();
        Check(f.OAuth.Redirect == new Uri(SpotifyRedirectUri.DesktopDefault), "Overlapping client changed pending URI");
        await f.Begin(SpotifyRedirectUri.VoxtaDefault);
        f.Clock.Advance(TimeSpan.FromMinutes(6));
        await Throws<InvalidOperationException>(() => f.Exchange());
        await f.Begin(SpotifyRedirectUri.DesktopDefault);
        var changed = new StaticSettingsSource(new() { ["ClientId"] = "different", ["ClientSecret"] = "encrypted:test-secret" });
        await Throws<InvalidOperationException>(() => f.Provider().ExchangeCodeForTokenAsync(f.Auth, changed, Field, "code", default));
        await f.Begin(SpotifyRedirectUri.VoxtaDefault);
        f.OAuth.ExchangeFailure = new InvalidOperationException("provider-failure");
        await Throws<InvalidOperationException>(() => f.Exchange());
        var calls = f.OAuth.ExchangeCalls;
        await Throws<InvalidOperationException>(() => f.Exchange());
        Check(f.OAuth.ExchangeCalls == calls, "Failed code exchange was retried");
        Console.WriteLine("PASS flow expiry, credential changes, overlapping-client guard and no redirect retries");

        f = new();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Throws<OperationCanceledException>(() => f.Begin(SpotifyRedirectUri.DesktopDefault, canceled.Token));
        await f.Begin(SpotifyRedirectUri.DesktopDefault);
        await Throws<OperationCanceledException>(() => f.Provider().ExchangeCodeForTokenAsync(f.Auth, f.Settings, Field, "code", canceled.Token));
        await f.Exchange();
        await f.Begin(SpotifyRedirectUri.VoxtaDefault);
        await f.Provider().SignOutAsync(f.Auth, f.Settings, Field, default);
        await Throws<InvalidOperationException>(() => f.Exchange());
        await f.Provider().SignOutAsync(f.Auth, f.Settings, Field, default);
        Check((await f.Provider().GetAuthorizationStatusAsync(f.Auth, f.Settings, Field, null, default)).Status == AuthorizationFlowStatus.Undefined, "Signed-out account authorized");
        Console.WriteLine("PASS cancellation, sign-out and pending-flow cleanup");

        await CheckTokenStorageAndManager();
    }

    private const string Field = "SpotifyAccount";
    private static async Task CheckTokenStorageAndManager()
    {
        var encryption = DispatchProxy.Create<ILocalEncryptionProvider, OAuthEncryptionProxy>();
        var backend = new FakeHostTokenStore();
        var user = Guid.NewGuid();
        var id = Guid.NewGuid();
        backend.Value = new("encrypted:host-access", DateTimeOffset.UtcNow.AddHours(1), "Spotify user", "encrypted:host-refresh", "scope");
        var storage = new SpotifyVoxtaTokenStore(backend, encryption, user, id);
        var token = await storage.LoadAsync(default);
        Check(token?.AccessToken == "host-access" && token.RefreshToken == "host-refresh", "Host token decryption");
        Check(backend.User == user && backend.Id == id, "Token read not user/token scoped");
        await storage.SaveAsync(new() { AccessToken = "new-access", RefreshToken = "new-refresh", ExpiresAt = DateTime.UtcNow.AddHours(1) }, default);
        Check(backend.Value!.EncryptedAccessToken == "encrypted:new-access" && backend.Value.EncryptedRefreshToken == "encrypted:new-refresh", "Refresh update not encrypted");
        Check(backend.Value.Scope == "scope" && backend.Value.Name == "Spotify user", "Token metadata lost");

        var oauth = new FakeOAuth();
        var config = new SpotifyManagerConfig { ClientId = "test-client", ClientSecret = "test-secret", UserId = user, TokenId = id };
        var manager = new SpotifyManager(config, storage, oauth, NullLogger<SpotifyManager>.Instance);
        await manager.InitializeSpotifyClient(default);
        Check(manager.HasClient && !manager.IsAuthorizationRequired, "Stored account token not used");
        await storage.DeleteAsync(default);
        Check(await manager.GetCurrentPlaybackState(default) == null && !manager.HasClient && manager.IsAuthorizationRequired, "Cached client survived sign-out");
        await Throws<InvalidOperationException>(() => storage.SaveAsync(new() { AccessToken = "access" }, default));
        manager = new(config, storage, oauth, NullLogger<SpotifyManager>.Instance);
        await manager.InitializeSpotifyClient(default);
        Check(!manager.HasClient && manager.LastUserVisibleError!.Contains("Spotify Account"), "Missing token did not guide sign-in");
        Check(oauth.ExchangeCalls == 0 && oauth.RefreshCalls == 0, "Manager initiated browser authorization");

        backend.Value = new("encrypted:expired", DateTimeOffset.UtcNow.AddHours(-1), "Spotify", "encrypted:refresh");
        manager = new(config, storage, oauth, NullLogger<SpotifyManager>.Instance);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => manager.InitializeSpotifyClient(default)));
        Check(manager.HasClient && oauth.RefreshCalls == 1, "Concurrent token refresh duplicated");
        Check(backend.Value!.EncryptedAccessToken == "encrypted:refreshed", "Refreshed account not saved");

        backend.Value = new("encrypted:expired", DateTimeOffset.UtcNow.AddHours(-1), "Spotify", "encrypted:refresh");
        oauth.RefreshFailure = new InvalidOperationException("invalid_grant");
        manager = new(config, storage, oauth, NullLogger<SpotifyManager>.Instance);
        await manager.InitializeSpotifyClient(default);
        Check(!manager.HasClient && manager.IsAuthorizationRequired && backend.Value == null, "Rejected refresh retained authorization");

        backend.Value = new("encrypted:expired", DateTimeOffset.UtcNow.AddHours(-1), "Spotify", "encrypted:refresh");
        oauth.RefreshFailure = new OperationCanceledException();
        manager = new(config, storage, oauth, NullLogger<SpotifyManager>.Instance);
        await Throws<OperationCanceledException>(() => manager.InitializeSpotifyClient(default));
        Check(backend.Value != null, "Cancellation deleted credentials");
        Console.WriteLine("PASS encrypted account storage, sign-out invalidation, missing credentials, serialized refresh, invalid grant and cancellation");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class Fixture
    {
        public readonly FakeOAuth OAuth = new();
        public readonly FakeClock Clock = new();
        public readonly SpotifyOAuthFlows Flows;
        public readonly IHttpContextAccessor Request = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        public readonly ILocalEncryptionProvider Encryption = DispatchProxy.Create<ILocalEncryptionProvider, OAuthEncryptionProxy>();
        public readonly StaticSettingsSource Settings = new(new()
        {
            ["ClientId"] = "test-client", ["ClientSecret"] = "encrypted:test-secret",
            // Obsolete persisted settings must not influence callback choice.
            ["AuthorizationMode"] = "listener", ["VoxtaRedirectUri"] = "invalid-old-uri", ["RedirectUri"] = "invalid-old-uri", ["TokenPath"] = "invalid-old-path",
        });
        public TestAuth Auth = new(Guid.NewGuid());
        public Guid Module
        {
            get => (Guid)Request.HttpContext!.Request.RouteValues["moduleId"]!;
            set => Request.HttpContext!.Request.RouteValues["moduleId"] = value;
        }
        public Fixture()
        {
            Flows = new(Clock);
            Request.HttpContext!.Request.Scheme = "http";
            Request.HttpContext.Request.Host = new HostString("127.0.0.1", 5384);
            Module = Guid.NewGuid();
        }
        public SpotifyModuleAuthorizationProvider Provider() => new(Encryption, OAuth, Flows, Request);
        public Task<string> Begin(string callback, CancellationToken token = default) => Provider().GetOAuth2AuthorizationUrlAsync(Auth, Settings, Field, "state-from-voxta", callback, token);
        public Task<AccessTokenInfo> Exchange() => Provider().ExchangeCodeForTokenAsync(Auth, Settings, Field, "code", default);
    }

    private sealed record TestAuth(Guid UserId) : IAuthenticationContext
    {
        public string Role => "USER";
        public string AccountRole => "USER";
        public bool Remote => false;
    }
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
    private sealed class FakeOAuth : ISpotifyOAuthTokenClient
    {
        public string? Secret;
        public Uri? Redirect;
        public int ExchangeCalls, RefreshCalls;
        public Exception? ExchangeFailure, RefreshFailure;
        public Task<AccessTokenInfo> ExchangeAsync(string clientId, string secret, string code, Uri redirect, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ExchangeCalls++; Secret = secret; Redirect = redirect;
            if (ExchangeFailure != null) throw ExchangeFailure;
            return Task.FromResult(new AccessTokenInfo("plain-access", DateTimeOffset.UtcNow.AddHours(1), "Spotify", "plain-refresh"));
        }
        public async Task<AccessTokenInfo> RefreshAsync(string clientId, string secret, string refreshToken, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RefreshCalls++;
            if (RefreshFailure != null) throw RefreshFailure;
            await Task.Yield();
            return new AccessTokenInfo("refreshed", DateTimeOffset.UtcNow.AddHours(1), "Spotify", refreshToken);
        }
    }
    private sealed class FakeHostTokenStore : IModuleOAuthTokenStore
    {
        public AccessTokenInfo? Value;
        public Guid User, Id;
        public Task<AccessTokenInfo?> GetAsync(Guid user, Guid id, CancellationToken token) { User = user; Id = id; return Task.FromResult(Value); }
        public Task<bool> UpdateAsync(Guid user, Guid id, AccessTokenInfo value, CancellationToken token) { Value = value; return Task.FromResult(true); }
        public Task DeleteAsync(Guid user, Guid id, CancellationToken token) { Value = null; return Task.CompletedTask; }
        public Task<Guid> SaveAsync(Guid user, Guid module, string field, Guid? id, AccessTokenInfo value, CancellationToken token) => throw new Exception("Host owns initial persistence");
        public Task<IReadOnlyDictionary<string, AccessTokenInfo>> ListByModuleAsync(Guid user, Guid module, CancellationToken token) => throw new NotSupportedException();
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
