using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using Voxta.Modules.Aios.Spotify.Clients.Models;
using Voxta.Modules.Aios.Spotify.Clients.Services;

internal static class SpotifyPlaybackChecks
{
    public static async Task RunAsync()
    {
        var http = new FakeHttp();
        var manager = await Create(http);
        Check(await manager.PlaySpecificUri("spotify:track:requested", default, "track"), "Confirmed track failed: " + manager.LastUserVisibleError + $" (lookups={http.TrackLookups}, plays={http.PlayBodies.Count}, polls={http.Polls})");
        using (var body = JsonDocument.Parse(http.PlayBodies.Single()))
        {
            var root = body.RootElement;
            Check(root.GetProperty("context_uri").GetString() == "spotify:album:album", "Album context missing");
            Check(root.GetProperty("offset").GetProperty("uri").GetString() == "spotify:track:resolved", "Resolved track offset missing");
            Check(!root.TryGetProperty("uris", out _), "Bare URI request still sent");
        }

        foreach (var state in new[]
        {
            "{\"is_playing\":false,\"item\":null}",
            "{\"is_playing\":true,\"item\":{\"type\":\"track\",\"uri\":\"spotify:track:other\"}}",
        })
        {
            http = new() { State = state };
            manager = await Create(http);
            Check(!await manager.PlaySpecificUri("spotify:track:requested", default, "track"), "Accepted request falsely reported playing");
            Check(manager.LastUserVisibleError?.Contains("could not be confirmed") == true, "Unconfirmed playback error missing");
            Check(http.PlayBodies.Count == 1 && http.Polls == 5, "Mutation retried or verification not bounded");
        }

        http = new() { EmptyPolls = 2 };
        manager = await Create(http);
        Check(await manager.PlaySpecificUri("spotify:track:requested", default), "Delayed playback or inferred track type failed");
        Check(http.Polls == 3 && http.PlayBodies.Count == 1, "Delayed playback retried mutation");

        http = new() { TrackStatus = HttpStatusCode.NotFound };
        manager = await Create(http);
        Check(!await manager.PlaySpecificUri("spotify:track:requested", default, "track") && http.PlayBodies.Count == 0, "Failed lookup changed playback");

        http = new() { State = "{\"is_playing\":true,\"context\":{\"uri\":\"spotify:playlist:list\"},\"item\":{\"type\":\"track\",\"uri\":\"spotify:track:other\"}}" };
        manager = await Create(http);
        Check(await manager.PlaySpecificUri("spotify:playlist:list", default, "playlist"), "Playlist confirmation failed");
        Check(http.TrackLookups == 0, "Playlist caused a track lookup");

        foreach (var context in new[] { "null", "{\"uri\":\"spotify:album:album\"}" })
        {
            http = new() { State = "{\"is_playing\":true,\"context\":" + context + ",\"device\":{\"name\":\"XENTOWER\"},\"item\":{\"type\":\"track\",\"name\":\"Rollin\",\"uri\":\"spotify:track:rollin\",\"artists\":[{\"name\":\"Limp Bizkit\",\"uri\":\"spotify:artist:limpbizkit\"}]}}" };
            manager = await Create(http);
            var result = await manager.PlayWithResult("spotify:artist:limpbizkit", default, "artist");
            Check(result.Accepted && result.Confirmed && result.Playback?.Item is FullTrack { Name: "Rollin" }, "Artist membership did not confirm tool playback");
            Check(http.PlayBodies.Count == 1 && http.Polls == 1 && http.TrackLookups == 0, "Artist request retried or fetched a track");
            using var artistBody = JsonDocument.Parse(http.PlayBodies.Single());
            Check(artistBody.RootElement.GetProperty("context_uri").GetString() == "spotify:artist:limpbizkit", "Artist context not sent to Spotify");
        }

        foreach (var state in new[]
        {
            "{\"is_playing\":false,\"item\":null}",
            "{\"is_playing\":true,\"item\":{\"type\":\"track\",\"uri\":\"spotify:track:other\",\"artists\":[{\"uri\":\"spotify:artist:other\"}]}}",
            "{\"is_playing\":false,\"item\":{\"type\":\"track\",\"artists\":[{\"uri\":\"spotify:artist:limpbizkit\"}]}}",
        })
        {
            http = new() { State = state };
            manager = await Create(http);
            var result = await manager.PlayWithResult("spotify:artist:limpbizkit", default, "artist");
            Check(result.Accepted && !result.Confirmed && result.Error == null && manager.LastUserVisibleError == null, "Unconfirmed artist playback classified as failed/confirmed");
            Check(http.PlayBodies.Count == 1 && http.Polls == 5, "Artist mutation retried or polling not bounded");
        }

        http = new() { State = "{\"is_playing\":true,\"item\":{\"type\":\"track\",\"artists\":[{\"uri\":\"spotify:artist:limpbizkit\"}]}}" };
        manager = await Create(http);
        Check(!await manager.PlaySpecificUri("spotify:artist:limpbizkit", default, "artist") && manager.LastUserVisibleError?.Contains("could not be confirmed") == true, "Legacy action confirmation behavior changed");

        http = new() { PlayStatus = HttpStatusCode.NotFound };
        manager = await Create(http);
        var rejected = await manager.PlayWithResult("spotify:artist:limpbizkit", default, "artist");
        Check(!rejected.Accepted && !rejected.Confirmed && rejected.Error != null && http.Polls == 0, "Rejected command reported accepted or polled");

        http = new() { PollStatus = HttpStatusCode.Forbidden };
        manager = await Create(http);
        var readFailed = await manager.PlayWithResult("spotify:artist:limpbizkit", default, "artist");
        Check(readFailed.Accepted && !readFailed.Confirmed && readFailed.Error != null && http.PlayBodies.Count == 1, "Read error lost accepted status or retried mutation");

        using var canceled = new CancellationTokenSource();
        http = new() { EmptyPolls = 5, AfterPoll = canceled.Cancel };
        manager = await Create(http);
        try
        {
            await manager.PlaySpecificUri("spotify:track:requested", canceled.Token, "track");
            throw new Exception("Canceled verification returned normally");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        Check(http.PlayBodies.Count == 1, "Cancellation retried playback");
        Console.WriteLine("PASS tool artist confirmation without context, observations, paused/wrong items, accepted vs rejected/read errors, and unchanged legacy confirmation");
        Console.WriteLine("PASS album-context playback, relinked track, delayed confirmation, empty/wrong playback, lookup errors, playlists and cancellation");
    }

    private static async Task<SpotifyManager> Create(FakeHttp http)
    {
        var manager = new SpotifyManager(new() { ClientId = "test", ClientSecret = "test" }, new TokenStore(), null!, NullLogger<SpotifyManager>.Instance);
        await manager.InitializeSpotifyClient(default);
        // Keep the real SDK serialization and response handling, replacing only its network transport.
        typeof(SpotifyManager).GetField("_spotifyClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken("test").WithHTTPClient(http)));
        return manager;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class TokenStore : ISpotifyTokenStore
    {
        public Task<SpotifyAuthToken?> LoadAsync(CancellationToken token) => Task.FromResult<SpotifyAuthToken?>(new() { AccessToken = "test", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        public Task SaveAsync(SpotifyAuthToken value, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteAsync(CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class FakeHttp : IHTTPClient
    {
        public readonly List<string> PlayBodies = [];
        public int Polls, TrackLookups, EmptyPolls;
        public HttpStatusCode TrackStatus = HttpStatusCode.OK;
        public HttpStatusCode PlayStatus = HttpStatusCode.NoContent, PollStatus = HttpStatusCode.OK;
        public Action? AfterPoll;
        public string State = "{\"is_playing\":true,\"item\":{\"type\":\"track\",\"uri\":\"spotify:track:resolved\"}}";
        public void SetRequestTimeout(TimeSpan timeout) { }
        public void Dispose() { }
        public Task<IResponse> DoRequest(IRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var status = HttpStatusCode.OK;
            string body;
            if (request.Method == HttpMethod.Put)
            {
                Check(request.Endpoint.ToString().TrimStart('/') == "me/player/play", "Unexpected mutation");
                PlayBodies.Add((string)request.Body!);
                status = PlayStatus;
                body = status == HttpStatusCode.NoContent ? "" : "{\"error\":{\"status\":404,\"message\":\"No active device found\"}}";
            }
            else if (request.Endpoint.ToString().TrimStart('/').StartsWith("tracks/"))
            {
                TrackLookups++;
                status = TrackStatus;
                body = status == HttpStatusCode.OK
                    ? "{\"uri\":\"spotify:track:resolved\",\"album\":{\"uri\":\"spotify:album:album\"}}"
                    : "{\"error\":{\"status\":404,\"message\":\"Track unavailable\"}}";
            }
            else
            {
                Polls++;
                status = PollStatus;
                body = Polls <= EmptyPolls ? "{\"is_playing\":false,\"item\":null}" : State;
                if (status != HttpStatusCode.OK) body = "{\"error\":{\"status\":403,\"message\":\"Forbidden\"}}";
                AfterPoll?.Invoke();
            }
            return Task.FromResult<IResponse>(new Response(new Dictionary<string, string>())
            {
                StatusCode = status, ContentType = "application/json", Body = body,
            });
        }
    }
}
