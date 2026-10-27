using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SpotifyAPI.Web;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Modules.Aios.Spotify.ChatAugmentations;
using Voxta.Modules.Aios.Spotify.Clients.Services;
using Voxta.Modules.Aios.Spotify.Tools;
using Voxta.Model.Shared;

internal static class SpotifyContextChecks
{
    public static async Task RunAsync()
    {
        var f = new Fixture();
        var api = DispatchProxy.Create<IChatSessionChatAugmentationApi, SessionProxy>();
        var session = (SessionProxy)api;
        var monitor = new SpotifyPlaybackMonitor(f.Manager, api, NullLogger<SpotifyPlaybackMonitor>.Instance)
        {
            ToolGuidance = SpotifyToolGuidance.Build(SpotifyToolCatalog.Choices.Where(c => c.EnabledByDefault).Select(c => c.Name).ToHashSet()),
        };
        string Context(string name) => session.Contexts.Single(c => c.Name == name).Text;

        f.Backend.State = null;
        var playback = new SpotifyPlaybackSession(monitor, f.Ducking);
        await playback.InitializeAsync(default);
        await playback.DisposeAsync();
        var tools = new SpotifyToolsSession(f.Catalog, playback);
        async Task CheckAvailability(bool active)
        {
            var offered = await tools.GetToolsAsync(default);
            Check(offered.Count == 16, "Dynamic availability removed discovery metadata");
            Check(offered.Where(t => t.Enabled).Select(t => t.Name).ToHashSet().SetEquals(
                active ? SpotifyToolCatalog.Choices.Select(c => c.Name) : Array.Empty<string>()), "Incorrect live tool availability");
        }
        await CheckAvailability(false);
        Check(session.Contexts.All(c => c.Name != "Spotify Tool Guidance"), "Guidance retained while tools unavailable");
        Check(session.Contexts.Count(c => c.Name == "Spotify Status") == 1
            && session.Contexts.All(c => c.Name is not ("Spotify Connection Status" or "Spotify Playback Status")), "Duplicate status contexts retained");
        Check(Context("Spotify Status").Contains("start playback manually"), "Manual playback prerequisite missing");
        Check(Context("Spotify Status").Contains("all Spotify tools are temporarily unavailable")
            && Context("Spotify Status").Contains("preferred device")
            && Context("Spotify Status").Contains("continue their original request")
            && Context("Spotify Status").Contains("automatically in the background"), "Context-only startup guidance missing");
        Check(Context("Spotify Status").Contains("no active player"), "No player misreported as paused");
        Check(session.Contexts.All(c => c.Name != "Spotify Available Devices"), "Unrequested discovery context retained");
        Check(f.Backend.Calls.All(c => c.Name == "GetCurrentPlaybackState"), "Preflight discovered or controlled devices");

        var writes = session.ContextWrites;
        await monitor.RefreshAsync(default);
        Check(session.ContextWrites == writes, "Unchanged tick republished context");
        var contextId = session.Contexts.Single(c => c.Name == "Spotify Status").Id;
        f.Backend.State = new() { Device = new() { Name = "Computer", Id = "private-pc-id", Type = "Computer", IsActive = true, VolumePercent = 50 }, IsPlaying = false };
        await monitor.RefreshAsync(default);
        await CheckAvailability(true);
        Check(Context("Spotify Tool Guidance").Contains("Never automatically retry") && !Context("Spotify Tool Guidance").Contains("device discovery"), "Enabled-tool guidance missing or device opt-out ignored");
        Check(Context("Spotify Active Device").Contains("Computer") && Context("Spotify Status").Contains("paused"), "Paused active player not recognized");
        Check(session.Contexts.All(c => !c.Text.Contains("private-pc-id")), "Internal device ID exposed in context");
        f.Backend.State = new() { Device = new() { Name = "Other Computer", Id = "other", Type = "Computer", IsActive = true, VolumePercent = 50 }, IsPlaying = true };
        await monitor.RefreshAsync(default);
        await CheckAvailability(true);
        Check(Context("Spotify Active Device").Contains("Other Computer") && Context("Spotify Status").Contains("playing"), "Device switch with same volume not published");
        Check(!Context("Spotify Status").Contains("temporarily unavailable"), "Startup guidance retained with active player");
        Check(session.Contexts.Single(c => c.Name == "Spotify Status").Id == contextId, "Context identity changed");
        f.Backend.State = null;
        await monitor.RefreshAsync(default);
        await CheckAvailability(false);
        Check(session.Contexts.All(c => c.Name != "Spotify Active Device"), "Stale active device retained");
        Check(session.Contexts.All(c => c.Name != "Spotify Last Command"), "Last command context retained");

        f.Backend.OnCall = name => { if (name == "GetCurrentPlaybackState") throw new Exception("secret-error"); };
        await monitor.RefreshAsync(default);
        await CheckAvailability(false);
        Check(Context("Spotify Status").Contains("unavailable") && !Context("Spotify Status").Contains("secret-error"), "Playback failure leaked details");
        f.Backend.OnCall = null;
        f.Backend.AuthorizationRequired = true;
        f.Backend.Error = "Spotify authorization is required.";
        await monitor.RefreshAsync(default);
        Check(Context("Spotify Status").Contains("authorization"), "Authorization misreported as no active player");
        await CheckAvailability(false);
        f.Backend.AuthorizationRequired = false;
        f.Backend.Error = null;
        await monitor.RefreshAsync(default);
        Check(Context("Spotify Status").Contains("start playback manually"), "Player state did not recover after reauthorization");
        Check(f.Backend.Calls.All(c => c.Name == "GetCurrentPlaybackState"), "Monitoring performed device discovery");

        var disabledCatalog = SpotifyToolCatalog.Create(f.Handler,
            SpotifyToolsSettings.From(new Voxta.Abstractions.Registration.StaticSettingsSource(new()
            {
                ["SpotifyTools"] = "[{\"name\":\"spotify_skip\",\"disabled\":true},{\"name\":\"spotify_search\",\"disabled\":true},{\"name\":\"spotify_list_devices\",\"disabled\":true},{\"name\":\"spotify_transfer_to_device\",\"disabled\":true}]",
            })),
            f.Playlists, NullLogger.Instance);
        var disabledSession = new SpotifyToolsSession(disabledCatalog, playback);
        foreach (var active in new[] { true, false, true })
        {
            f.Backend.State = active ? new() { Device = new() { IsActive = true }, IsPlaying = false } : null;
            await monitor.RefreshAsync(default);
            Check((await disabledSession.GetToolsAsync(default)).Where(t => t.Name is "spotify_skip" or "spotify_search" or "spotify_list_devices" or "spotify_transfer_to_device").All(t => !t.Enabled), "Reconnection overrode saved switches");
        }
        Check((await disabledSession.CallToolAsync("spotify_skip", null, default)).IsError, "Saved disabled tool executed");
        Check((await disabledSession.CallToolAsync("spotify_list_devices", null, default)).IsError
            && (await disabledSession.CallToolAsync("spotify_transfer_to_device", null, default)).IsError, "Disabled device tool executed");
        Check(session.FlagWrites == 0, "Playback monitoring still writes legacy flags");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var count = f.Backend.Calls.Count;
        try { await monitor.RefreshAsync(canceled.Token); throw new Exception("Cancellation swallowed"); }
        catch (OperationCanceledException) { }
        Check(f.Backend.Calls.Count == count, "Canceled refresh accessed Spotify");

        // A generic device/404 failure must not be presented as a missing active player.
        var manager = new SpotifyManager(new() { ClientId = "test", ClientSecret = "test" }, null!, null!, NullLogger<SpotifyManager>.Instance);
        var setError = typeof(SpotifyManager).GetMethod("SetSpotifyApiError", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var (message, missingPlayer) in new[]
        {
            ("Player command failed: No active device found", true),
            ("NO_ACTIVE_DEVICE", true),
            ("404 Not Found", false),
            ("Device is restricted", false),
        })
        {
            setError.Invoke(manager, [new APIException(message), "start playback"]);
            Check((manager.LastUserVisibleError == SpotifyManager.NoActiveDeviceMessage) == missingPlayer, "Provider failure misclassified: " + message);
        }
        Console.WriteLine("PASS playback-only preflight, manual startup guidance, paused/playing context, failures and cancellation");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
