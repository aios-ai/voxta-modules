using System.Net;
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
        await CheckShuffleAndFavorites();
        var http = new FakeHttp();
        var manager = await Create(http);
        Check((await manager.PlayWithResult("spotify:track:requested", default, "track")).Confirmed, "Confirmed track failed: " + manager.LastUserVisibleError + $" (lookups={http.TrackLookups}, plays={http.PlayBodies.Count}, polls={http.Polls})");
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
            Check(!(await manager.PlayWithResult("spotify:track:requested", default, "track")).Confirmed, "Accepted request falsely reported playing");
            Check(manager.LastUserVisibleError == null, "Accepted unconfirmed playback reported as failed");
            Check(http.PlayBodies.Count == 1 && http.Polls == 5, "Mutation retried or verification not bounded");
        }

        http = new() { EmptyPolls = 2 };
        manager = await Create(http);
        Check((await manager.PlayWithResult("spotify:track:requested", default)).Confirmed, "Delayed playback or inferred track type failed");
        Check(http.Polls == 3 && http.PlayBodies.Count == 1, "Delayed playback retried mutation");

        http = new() { TrackStatus = HttpStatusCode.NotFound };
        manager = await Create(http);
        Check(!(await manager.PlayWithResult("spotify:track:requested", default, "track")).Confirmed && http.PlayBodies.Count == 0, "Failed lookup changed playback");

        http = new() { State = "{\"is_playing\":true,\"context\":{\"uri\":\"spotify:playlist:list\"},\"item\":{\"type\":\"track\",\"uri\":\"spotify:track:other\"}}" };
        manager = await Create(http);
        Check((await manager.PlayWithResult("spotify:playlist:list", default, "playlist")).Confirmed, "Playlist confirmation failed");
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
            await manager.PlayWithResult("spotify:track:requested", canceled.Token, "track");
            throw new Exception("Canceled verification returned normally");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        Check(http.PlayBodies.Count == 1, "Cancellation retried playback");
        Console.WriteLine("PASS tool artist confirmation without context, observations, paused/wrong items, accepted vs rejected/read errors");
        Console.WriteLine("PASS album-context playback, relinked track, delayed confirmation, empty/wrong playback, lookup errors, playlists and cancellation");
    }

    private static async Task<SpotifyManager> Create(FakeHttp http)
    {
        var manager = new SpotifyManager(new() { ClientId = "test", ClientSecret = "test" }, new TokenStore(), null!, NullLogger<SpotifyManager>.Instance);
        await manager.InitializeSpotifyClient(default);
        // Keep the real SDK serialization and response handling, replacing only its network transport.
        manager.ConfigureClient(SpotifyClientConfig.CreateDefault().WithToken("test").WithHTTPClient(http));
        return manager;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static async Task CheckShuffleAndFavorites()
    {
        foreach (var desired in new[] { true, false })
        {
            var http = new FakeHttp { State = "{\"device\":{\"id\":\"desktop\"},\"shuffle_state\":" + desired.ToString().ToLowerInvariant() + "}", EmptyPolls = 2 };
            var manager = await Create(http);
            Check(await manager.SetShuffle(desired, default), "Delayed shuffle was not confirmed");
            Check(http.ShuffleRequests == 1 && http.Polls == 3, "Shuffle mutation retried or delayed observation ignored");
            Check(http.ShuffleParameter == desired.ToString().ToLowerInvariant(), "Wrong shuffle mode sent");

            http = new() { State = "{\"device\":{\"id\":\"desktop\"},\"shuffle_state\":" + (!desired).ToString().ToLowerInvariant() + "}" };
            manager = await Create(http);
            Check(!await manager.SetShuffle(desired, default) && manager.LastUserVisibleError?.Contains("could not be confirmed") == true, "Accepted but unchanged shuffle reported success");
            Check(http.ShuffleRequests == 1 && http.Polls == 5, "Shuffle verification unbounded or command retried");
        }
        foreach (var state in new[] { "null", "{\"shuffle_state\":false}" })
        {
            var http = new FakeHttp { State = state };
            var manager = await Create(http);
            Check(!await manager.SetShuffle(false, default), "Missing player falsely confirmed shuffle off");
        }
        foreach (var failure in new[] { "write", "read", "cancel" })
        {
            using var canceled = new CancellationTokenSource();
            var http = new FakeHttp { ShuffleStatus = failure == "write" ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent,
                PollStatus = failure == "read" ? HttpStatusCode.Forbidden : HttpStatusCode.OK,
                AfterPoll = failure == "cancel" ? canceled.Cancel : null };
            var manager = await Create(http);
            try
            {
                Check(!await manager.SetShuffle(true, canceled.Token), "Shuffle error reported success");
                Check(failure != "cancel", "Cancellation swallowed");
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
            Check(http.ShuffleRequests == 1, "Failed shuffle command retried");
        }
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest })
        {
            var http = new FakeHttp { LibraryStatus = status };
            var manager = await Create(http);
            Check(await manager.AddTrackToLibraryAsync("spotify:track:requested", "Track", default) == (status == HttpStatusCode.OK ? SpotifyLibrarySaveResult.Saved : SpotifyLibrarySaveResult.Failed), "Favorites result incorrect");
            Check(http.LibraryChecks == 1 && http.LibraryRequests == 1, "Favorites check/save retried");
        }
        foreach (var (body, status, expected) in new[]
        {
            ("[true]", HttpStatusCode.OK, SpotifyLibrarySaveResult.AlreadySaved),
            ("[]", HttpStatusCode.OK, SpotifyLibrarySaveResult.Failed),
            ("[false,true]", HttpStatusCode.OK, SpotifyLibrarySaveResult.Failed),
            ("{\"error\":{\"status\":403,\"message\":\"Forbidden\"}}", HttpStatusCode.Forbidden, SpotifyLibrarySaveResult.Failed),
        })
        {
            var http = new FakeHttp { LibraryContainsBody = body, LibraryContainsStatus = status };
            var manager = await Create(http);
            Check(await manager.AddTrackToLibraryAsync("spotify:track:requested", "Track", default) == expected, "Membership check result incorrect");
            Check(http.LibraryChecks == 1 && http.LibraryRequests == 0, "Already saved/failed membership check changed library or retried");
        }
        using (var canceled = new CancellationTokenSource())
        {
            var http = new FakeHttp { AfterLibraryCheck = canceled.Cancel };
            var manager = await Create(http);
            try
            {
                await manager.AddTrackToLibraryAsync("spotify:track:requested", "Track", canceled.Token);
                throw new Exception("Canceled membership check returned normally");
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
            Check(http.LibraryChecks == 1 && http.LibraryRequests == 0, "Canceled membership check saved track");
        }
        Console.WriteLine("PASS shuffle observation, delayed/unchanged/missing player, errors/cancellation, and favorites query transport");
    }

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
        public int ShuffleRequests, LibraryRequests, LibraryChecks;
        public string LibraryContainsBody = "[false]";
        public HttpStatusCode LibraryContainsStatus = HttpStatusCode.OK;
        public Action? AfterLibraryCheck;
        public string? ShuffleParameter;
        public HttpStatusCode ShuffleStatus = HttpStatusCode.NoContent, LibraryStatus = HttpStatusCode.OK;
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
            if (request.Method == HttpMethod.Put && request.Endpoint.ToString().TrimStart('/') == "me/library")
            {
                LibraryRequests++;
                Check(request.Parameters?["uris"] == "spotify:track:requested", "Favorites URI missing from query parameters");
                Check(request.Body == null, "Favorites URI sent in body");
                status = LibraryStatus;
                body = status == HttpStatusCode.OK ? "" : "{\"error\":{\"status\":400,\"message\":\"Bad request\"}}";
            }
            else if (request.Method == HttpMethod.Get && request.Endpoint.ToString().TrimStart('/') == "me/library/contains")
            {
                LibraryChecks++;
                Check(request.Parameters?["uris"] == "spotify:track:requested", "Membership URI missing from query parameters");
                status = LibraryContainsStatus;
                body = LibraryContainsBody;
                AfterLibraryCheck?.Invoke();
            }
            else if (request.Method == HttpMethod.Put && request.Endpoint.ToString().TrimStart('/') == "me/player/shuffle")
            {
                ShuffleRequests++;
                ShuffleParameter = request.Parameters?["state"];
                status = ShuffleStatus;
                body = status == HttpStatusCode.NoContent ? "" : "{\"error\":{\"status\":403,\"message\":\"Forbidden\"}}";
            }
            else if (request.Method == HttpMethod.Put)
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
