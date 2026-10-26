using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Abstractions.Utils;
using Voxta.Modules.Aios.Spotify.Clients.Services;
using Voxta.Modules.Aios.Spotify.Clients.Models;
using Voxta.Modules.Aios.Spotify.Configuration;

internal static class SpotifyOAuthChecks
{
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var uri = FreeCallback();
        var callbacks = new SpotifyAuthCallbackManager();
        var code = callbacks.WaitForCodeAsync("expected-state", timeout.Token);
        using var http = new HttpClient();
        await using (var listener = await SpotifyOAuthListener.StartAsync(uri, callbacks, timeout.Token))
        {
            Check((await http.GetAsync(uri + "?code=test-code&state=wrong-state", timeout.Token)).StatusCode == HttpStatusCode.Gone, "Wrong state accepted");
            Check(!code.IsCompleted, "Wrong state consumed pending request");
            Check((await http.GetAsync(uri, timeout.Token)).StatusCode == HttpStatusCode.BadRequest, "Missing query accepted");
            var response = await http.GetAsync(uri + "?code=test-code&state=expected-state", timeout.Token);
            Check(response.IsSuccessStatusCode && await code == "test-code", "Callback code not delivered");
            Check(response.Headers.CacheControl?.NoStore == true, "Callback must not be cached");
            Check((await http.GetAsync(uri + "?code=test-code&state=expected-state", timeout.Token)).StatusCode == HttpStatusCode.Gone, "Callback replay accepted");
        }
        // Rebinding proves the temporary listener is stopped and the port released.
        await using (var listener = await SpotifyOAuthListener.StartAsync(uri, callbacks, timeout.Token))
        {
            var denied = callbacks.WaitForCodeAsync("denial-state", timeout.Token);
            Check((await http.GetAsync(uri + "?error=access_denied&state=denial-state", timeout.Token)).IsSuccessStatusCode, "Denial callback failed");
            try { await denied; throw new Exception("Denial left authorization pending"); }
            catch (SpotifyAuthorizationDeniedException) { }
        }
        var interaction = new WaitingInteraction();
        var manager = new SpotifyManager(callbacks, interaction, new SpotifyManagerConfig
        {
            ClientId = "test-client", ClientSecret = "test-secret", RedirectUri = FreeCallback(),
            TokenPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            AuthorizationTimeout = TimeSpan.FromSeconds(1),
        }, NullLogger<SpotifyManager>.Instance);
        await manager.InitializeSpotifyClient(timeout.Token);
        Check(manager.IsAuthorizationRequired && manager.LastUserVisibleError!.Contains("timed out"), "Authorization timeout not actionable");
        Check(interaction.Token?.Disposed == true, "Timed-out interaction not disposed");
        Check(callbacks.Callback("test-code", "unknown") == SpotifyAuthCallbackResult.NoPendingAuthorization, "Timeout retained pending state");

        var tokenPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var savedInteraction = new WaitingInteraction();
        var saved = new SpotifyManager(callbacks, savedInteraction, new SpotifyManagerConfig
        {
            ClientId = "test-client", ClientSecret = "test-secret",
            RedirectUri = new Uri("http://127.0.0.1:5384/extensions/spotify/oauth2/callback"), TokenPath = tokenPath,
        }, NullLogger<SpotifyManager>.Instance);
        try
        {
            await File.WriteAllTextAsync(tokenPath, JsonSerializer.Serialize(new SpotifyAuthToken { AccessToken = "test-access-token", ExpiresAt = DateTime.UtcNow.AddHours(1) }), timeout.Token);
            await saved.InitializeSpotifyClient(timeout.Token);
            Check(saved.HasClient && savedInteraction.Token == null, "Usable saved token unnecessarily required callback setup");
        }
        finally { File.Delete(tokenPath); }
        Console.WriteLine("PASS real loopback HTTP callback, state validation, replay, denial, listener disposal and authorization timeout (no Spotify account/network calls)");
    }

    private static Uri FreeCallback()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return new($"http://127.0.0.1:{port}{SpotifyRedirectUri.CallbackPath}");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class WaitingInteraction : ISpotifyUserInteractionWrapper
    {
        public CancellationToken Abort => default;
        public WaitingToken? Token;
        public Task<IUserInteractionRequestToken> RequestUserInteraction(Uri uri, bool reauthorize, CancellationToken token)
        {
            Token = new WaitingToken(token);
            return Task.FromResult<IUserInteractionRequestToken>(Token);
        }
    }

    private sealed class WaitingToken(CancellationToken token) : IUserInteractionRequestToken
    {
        public Guid RequestId => Guid.Empty;
        public Task<UserInteractionRequestResult> Task { get; } = Wait(token);
        public bool Disposed;
        public void Cancel() { }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        private static async Task<UserInteractionRequestResult> Wait(CancellationToken token)
        {
            await System.Threading.Tasks.Task.Delay(Timeout.Infinite, token);
            return default!;
        }
    }
}
