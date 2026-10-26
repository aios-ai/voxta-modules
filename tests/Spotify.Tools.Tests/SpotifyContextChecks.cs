using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SpotifyAPI.Web;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Modules.Aios.Spotify.ChatAugmentations;
using Voxta.Modules.Aios.Spotify.Clients.Services;
using Voxta.Modules.Aios.Spotify.Tools;

internal static class SpotifyContextChecks
{
    public static async Task RunAsync()
    {
        var f = new Fixture();
        var api = DispatchProxy.Create<IChatSessionChatAugmentationApi, SessionProxy>();
        var session = (SessionProxy)api;
        var clock = new TestClock();
        var monitor = new SpotifyPlaybackMonitor(f.Manager, api, NullLogger<SpotifyPlaybackMonitor>.Instance, clock);
        string Context(string name) => session.Contexts.Single(c => c.Name == name).Text;

        // Activation waits for the initial read, without making a tool call or a mutation.
        var ready = new TaskCompletionSource<IReadOnlyList<Device>>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Backend.State = null;
        f.Backend.Devices = [new() { Name = "Desktop", Id = "pc", Type = "Computer" }];
        f.Backend.PendingDevices = ready.Task;
        var playback = new SpotifyPlaybackSession(monitor, f.Ducking);
        var activation = playback.InitializeAsync(default);
        Check(!activation.IsCompleted && session.ContextWrites == 0, "Activation completed before discovery");
        ready.SetResult(f.Backend.Devices);
        await activation;
        await playback.DisposeAsync();
        f.Backend.PendingDevices = null;
        Check(Context("Spotify Available Devices").Contains("Desktop") && Context("Spotify Available Devices").Contains("\"active\":false"), "Preflight omitted inactive available device");
        Check(Context("Spotify Connection Status").Contains("No active") && !Context("Spotify Connection Status").Contains("disconnected"), "Availability confused with connection");
        Check(Context("Spotify Playback Status").Contains("no active player"), "No player misreported as paused");
        Check(f.Commands.Count == 0 && f.Backend.Calls.All(c => c.Name is "GetCurrentPlaybackState" or "GetAvailablePlaybackDevices"), "Preflight performed a command");

        var contextId = session.Contexts.Single(c => c.Name == "Spotify Available Devices").Id;
        var writes = session.ContextWrites;
        await monitor.RefreshAsync(default);
        Check(session.ContextWrites == writes && f.Backend.Calls.Count(c => c.Name == "GetAvailablePlaybackDevices") == 1, "Unchanged tick republished or rediscovered devices");
        f.Backend.Devices = [new() { Name = "New Computer", Id = "new", Type = "Computer" }];
        clock.Advance(TimeSpan.FromSeconds(30));
        await monitor.RefreshAsync(default);
        Check(Context("Spotify Available Devices").Contains("New Computer") && !Context("Spotify Available Devices").Contains("Desktop"), "Background discovery not refreshed");
        Check(session.Contexts.Single(c => c.Name == "Spotify Available Devices").Id == contextId, "Device context identity changed");

        f.Backend.State = new() { Device = new() { Name = "New Computer", Id = "new", Type = "Computer", IsActive = true, VolumePercent = 50 }, IsPlaying = false };
        await monitor.RefreshAsync(default);
        Check(Context("Spotify Active Device").Contains("New Computer") && Context("Spotify Playback Status").Contains("paused"), "Paused active player not recognized");
        f.Backend.State = new() { Device = new() { Name = "Other Computer", Id = "other", Type = "Computer", IsActive = true, VolumePercent = 50 }, IsPlaying = true };
        await monitor.RefreshAsync(default);
        Check(Context("Spotify Active Device").Contains("Other Computer") && Context("Spotify Playback Status").Contains("playing"), "Device switch with same volume not published");
        f.Backend.State = null;
        await monitor.RefreshAsync(default);
        Check(session.Contexts.All(c => c.Name != "Spotify Active Device"), "Stale active device retained");

        // Explicit discovery updates the same context immediately, even within the background cache interval.
        var handler = new SpotifyToolHandler(f.Manager, new(f.Manager, NullLogger<SpotifySearchService>.Instance), f.Playlists,
            f.Ducking, monitor.SetLastActionAsync, monitor.SetDevicesAsync);
        f.Backend.Devices = [new() { Name = "Fresh Desktop", Id = "fresh", Type = "Computer" }];
        Check(!(await handler.ExecuteAsync("list_devices", null, default)).IsError && Context("Spotify Available Devices").Contains("Fresh Desktop"), "Tool discovery did not refresh context");
        Check(!(await handler.ExecuteAsync("play_random_music", null, default)).IsError, "Playback request failed");
        Check(!f.Backend.Calls.Any(c => c.Name == "TransferPlayback"), "Context caused an automatic transfer");

        f.Backend.OnCall = name => { if (name == "GetAvailablePlaybackDevices") throw new Exception("secret-device-error"); };
        await monitor.RefreshAsync(default, true);
        Check(Context("Spotify Available Devices").Contains("unavailable") && !Context("Spotify Available Devices").Contains("Fresh Desktop") && !Context("Spotify Available Devices").Contains("secret-device-error"), "Discovery failure leaked details or stale devices");
        f.Backend.OnCall = null;
        f.Backend.AuthorizationRequired = true;
        f.Backend.Error = "Spotify authorization is required.";
        await monitor.RefreshAsync(default);
        Check(Context("Spotify Connection Status").Contains("authorization") && Context("Spotify Playback Status").Contains("unknown"), "Authorization misreported as no active player");
        f.Backend.AuthorizationRequired = false;
        f.Backend.Error = null;
        await monitor.RefreshAsync(default);
        Check(Context("Spotify Available Devices").Contains("Fresh Desktop"), "Discovery failed to recover after reauthorization");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var count = f.Backend.Calls.Count;
        try { await monitor.RefreshAsync(canceled.Token, true); throw new Exception("Cancellation swallowed"); }
        catch (OperationCanceledException) { }
        Check(f.Backend.Calls.Count == count, "Canceled refresh accessed Spotify");
        Console.WriteLine("PASS preflight context, inactive/paused/playing states, refresh cadence, tool refresh, failures and cancellation");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }
}
