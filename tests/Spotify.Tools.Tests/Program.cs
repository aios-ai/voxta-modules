using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SpotifyAPI.Web;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.Spotify;
using Voxta.Modules.Aios.Spotify.ChatAugmentations;
using Voxta.Modules.Aios.Spotify.Clients.Services;
using Voxta.Modules.Aios.Spotify.Tools;
using Voxta.Modules.Aios.Tools;

var operations = new (string Operation, string Json, string? Mutation)[]
{
    ("toggle_playback", "{}", "ControlSpotifyPlayback"),
    ("connect", "{}", null),
    ("play_random_music", "{}", "PlaySpecificUri"),
    ("play_special_playlist", "{\"name\":\"Daily Mix 1\"}", "PlaySpecificUri"),
    ("play_music", "{\"name\":\"Track\",\"type\":\"track\"}", "PlaySpecificUri"),
    ("queue_track", "{\"name\":\"Track\"}", "QueueTrack"),
    ("volume", "{\"type\":\"set\",\"value\":30}", "ChangeVolume"),
    ("seek_playback", "{\"target\":\"forward\"}", "SeekPlayback"),
    ("skip_next", "{}", "SkipToPreviousOrNextTrack"),
    ("skip_previous", "{}", "SkipToPreviousOrNextTrack"),
    ("repeat_mode", "{\"mode\":\"context\"}", "SetRepeatMode"),
    ("shuffle_mode", "{\"mode\":\"on\"}", "SetShuffle"),
    ("add_to_favorites", "{}", "AddTrackToLibraryAsync"),
    ("get_playlists", "{}", null),
    ("add_to_playlist", "{\"playlist\":\"Favorites\"}", "AddItems"),
    ("list_devices", "{}", null),
    ("transfer_to_device", "{\"device\":\"Desktop\"}", "TransferPlayback"),
};

foreach (var (operation, json, mutation) in operations)
{
    var fixture = new Fixture();
    var result = await fixture.Call(operation, json);
    Check(!result.IsError, $"Unexpected result for {operation}: {result.Text}");
    if (mutation != null)
    {
        Check(fixture.Backend.Calls.Count(c => c.Name == mutation) == 1, $"Duplicate or missing mutation: {operation}");
        var failed = new Fixture();
        failed.Backend.Succeed = false;
        Check((await failed.Call(operation, json)).IsError, $"Provider failure hidden: {operation}");
        Check(failed.Backend.Calls.Count(c => c.Name == mutation) == 1, $"Mutation retried: {operation}");
    }
    Console.WriteLine($"PASS {operation} dispatch and result");
}

var invalid = new (string, string)[]
{
    ("play_music", "{\"name\":\"Track\",\"type\":\"invalid\"}"),
    ("play_music", "{\"name\":3,\"type\":\"track\"}"),
    ("queue_track", "{\"name\":\" \"}"),
    ("play_special_playlist", "{}"),
    ("volume", "{\"type\":\"set\"}"),
    ("volume", "{\"type\":\"set\",\"value\":101}"),
    ("volume", "{\"type\":\"increase\",\"value\":-1}"),
    ("volume", "{\"type\":\"set\",\"value\":\"30\"}"),
    ("volume", "{\"type\":\"set\",\"value\":1.5}"),
    ("seek_playback", "{\"target\":\"to_time\"}"),
    ("seek_playback", "{\"target\":\"to_percent\",\"value\":101}"),
    ("seek_playback", "{\"target\":\"forward_percent\"}"),
    ("seek_playback", "{\"target\":\"forward_percent\",\"value\":101}"),
    ("seek_playback", "{\"target\":\"backward_percent\",\"value\":-1}"),
    ("seek_playback", "{\"target\":\"invalid\"}"),
    ("repeat_mode", "{\"mode\":\"invalid\"}"),
    ("shuffle_mode", "{\"mode\":true}"),
    ("add_to_playlist", "{}"),
    ("transfer_to_device", "{}"),
};
foreach (var (operation, json) in invalid)
{
    var fixture = new Fixture();
    Check((await fixture.Call(operation, json)).IsError, $"Invalid input accepted: {operation} {json}");
    Check(fixture.Backend.Calls.Count == 0, $"Invalid input reached provider: {operation}");
}
Console.WriteLine("PASS invalid arguments rejected before provider access");

{
    var f = new Fixture();
    var definitions = f.Catalog.GetTools();
    Check(definitions.Count == 17 && definitions.Select(d => d.Name).Distinct().Count() == 17, "Catalog coverage");
    Check(definitions.All(d => d.Source == VoxtaModule.ServiceName && d.AugmentationKey == VoxtaModule.ToolsAugmentationKey && !d.AlwaysAllowed && d.ExternalContent), "Tool ownership/permissions");
    Check(definitions.Count(d => d.Permission == ToolPermissionClass.Read) == 3, "Read/Act classification");
    Check(SpotifyToolCatalog.Choices.All(c => c.EnabledByDefault), "Tools should default to enabled");
    var settings = new SpotifyToolsSettings(new HashSet<string>(), [ChatStyles.Assistant]);
    var disabled = SpotifyToolCatalog.Create(f.Handler, settings, f.Playlists, NullLogger.Instance);
    Check(disabled.GetTools().All(t => !t.Enabled), "Disabled choices not preserved");
    Check((await disabled.CallToolAsync("spotify_skip_next", null, default)).IsError && f.Backend.Calls.Count == 0, "Disabled tool executed");
    Check((await f.Call("seek_playback", "{\"target\":\"forward\"}")).IsError == false, "Default seek failed");
    Check((int)f.Backend.Calls.Last(c => c.Name == "SeekPlayback").Args[0]! == 30000, "Default forward seek is not 10s");
    await f.Call("seek_playback", "{\"target\":\"to_time\",\"value\":2147483647}");
    Check((int)f.Backend.Calls.Last(c => c.Name == "SeekPlayback").Args[0]! == 180000, "Seek overflow/clamping");
    Console.WriteLine("PASS metadata, opt-in availability, seek defaults and overflow");
}

{
    // At 3:00 in a ten-minute track, moving by 10% means one minute from now.
    var f = new Fixture();
    f.Backend.State!.ProgressMs = 180000;
    ((FullTrack)f.Backend.State.Item).DurationMs = 600000;
    foreach (var (target, value, expected) in new[]
    {
        ("forward_percent", 10, 240000),
        ("backward_percent", 10, 120000),
        ("to_percent", 10, 60000),
        ("forward_percent", 100, 600000),
        ("backward_percent", 100, 0),
        ("forward_percent", 0, 180000),
    })
    {
        var result = await f.Call("seek_playback", JsonSerializer.Serialize(new { target, value }));
        Check(!result.IsError, "Percentage seek failed: " + target);
        Check((int)f.Backend.Calls.Last(c => c.Name == "SeekPlayback").Args[0]! == expected, "Incorrect seek reference or boundary: " + target);
    }
    f.Backend.State.Item = new FullEpisode { DurationMs = 600000 };
    Check(!(await f.Call("seek_playback", "{\"target\":\"forward_percent\",\"value\":10}")).IsError
        && (int)f.Backend.Calls.Last(c => c.Name == "SeekPlayback").Args[0]! == 240000, "Episode relative seek failed");
    Console.WriteLine("PASS relative/absolute percentage seeks, boundaries and episodes");
}

{
    var f = new Fixture();
    f.Backend.Devices = [new() { Name = "Kitchen One", Id = "1" }, new() { Name = "Kitchen Two", Id = "2" }];
    Check((await f.Call("transfer_to_device", "{\"device\":\"Kitchen\"}")).IsError, "Ambiguous device selected");
    Check(!f.Backend.Calls.Any(c => c.Name == "TransferPlayback"), "Ambiguous transfer side effect");
    Check((await f.Call("play_special_playlist", "{\"name\":\"Unknown\"}")).IsError, "Unconfigured playlist accepted");
    Check((await f.Call("add_to_playlist", "{\"playlist\":\"Unknown\"}")).IsError, "Unknown playlist accepted");
    f.Backend.State = null;
    foreach (var (operation, json) in new[] { ("toggle_playback", "{}"), ("add_to_favorites", "{}"), ("seek_playback", "{\"target\":\"middle\"}"), ("volume", "{\"type\":\"set\",\"value\":30}") })
        Check((await f.Call(operation, json)).IsError, "Missing playback state accepted");
    Console.WriteLine("PASS domain errors and ambiguous device safety");
}

{
    var f = new Fixture();
    f.Backend.State = null;
    f.Backend.Devices =
    [
        new() { Name = "XENTOWER", Id = "desktop", Type = "Computer", IsActive = false },
        new() { Name = "Echo", Id = "speaker", Type = "Speaker", IsActive = true, IsRestricted = true },
        new() { Name = "Echo", Id = "other-speaker", Type = "Speaker" },
        new() { Name = "Restricted", Id = "restricted-id", Type = "Speaker", IsRestricted = true },
        new() { Name = "Unavailable", Type = "Speaker" },
    ];
    foreach (var operation in new[] { "list_devices", "connect" })
    {
        var devices = await f.Call(operation, "{}");
        Check(!devices.IsError && devices.Text.Contains("XENTOWER [type: Computer, active: false, controllable: true"), "Inactive desktop metadata missing");
        Check(devices.Text.Split("Echo [").Length == 3 && devices.Text.Contains("controllable: false"), "Duplicate/restricted devices lost");
        Check(!devices.Text.Contains("id:") && !devices.Text.Contains("other-speaker") && !devices.Text.Contains("desktop"), "Internal IDs exposed by discovery");
    }
    Check(!f.Backend.Calls.Any(c => c.Name == "TransferPlayback"), "Discovery activated a player");
    Check((await f.Call("transfer_to_device", "{\"device\":\"Echo\"}")).IsError, "Duplicate names collapsed");
    Check((await f.Call("transfer_to_device", "{\"device\":\"Restricted\"}")).IsError, "Restricted device accepted");
    Check((await f.Call("transfer_to_device", "{\"device\":\"Unavailable\"}")).IsError, "Missing device ID accepted");
    Check(!f.Backend.Calls.Any(c => c.Name == "TransferPlayback"), "Invalid device selection transferred playback");
    Check((await f.Call("transfer_to_device", "{\"device\":\"other-speaker\"}")).IsError, "Internal ID accepted as user-facing device name");
    Check(!(await f.Call("transfer_to_device", "{\"device\":\"xentower\"}")).IsError, "Inactive device could not be selected");
    Check(f.Backend.Calls.Count(c => c.Name == "TransferPlayback") == 1 && (string)f.Backend.Calls.Single(c => c.Name == "TransferPlayback").Args[0]! == "desktop", "Name not privately resolved to ID, transfer retried or original command executed automatically");
    var missing = await f.Call("toggle_playback", "{}");
    Check(missing.IsError && missing.Text.Contains("select a track or playlist") && missing.Text.Contains("start playback manually") && !missing.Text.Contains("transfer"), "Missing player recovery absent");
    f.Backend.Succeed = false;
    f.Backend.Error = SpotifyManager.NoActiveDeviceMessage;
    var discoveryCount = f.Backend.Calls.Count(c => c.Name == "GetAvailablePlaybackDevices");
    Check((await f.Call("play_random_music", "{}")).Text.Contains("start playback manually"), "Playback failure did not request user setup");
    Check(f.Backend.Calls.Count(c => c.Name == "GetAvailablePlaybackDevices") == discoveryCount && f.Backend.Calls.Count(c => c.Name == "TransferPlayback") == 1, "Playback failure triggered discovery or transfer");
    f.Backend.Error = null;
    f.Backend.FailureSeen = false;
    f.Backend.Devices = [];
    var empty = await f.Call("list_devices", "{}");
    Check(!empty.IsError && empty.Text.Contains("start playback manually"), "Empty discovery not actionable");
    Console.WriteLine("PASS device recovery, metadata, duplicate IDs, restricted devices and no implicit transfer/retry");
}

{
    var f = new Fixture();
    using var cts = new CancellationTokenSource();
    f.Backend.OnCall = name => { if (name == "SearchSpotify") cts.Cancel(); };
    await Canceled(() => f.Call("play_music", "{\"name\":\"Track\",\"type\":\"track\"}", cts.Token));
    Check(!f.Backend.Calls.Any(c => c.Name == "PlaySpecificUri"), "Canceled search started playback");
    Check(f.Backend.Calls.Single(c => c.Name == "SearchSpotify").Args.OfType<CancellationToken>().Single() == cts.Token, "Search token lost");
    foreach (var (operation, json, _) in operations)
    {
        var canceled = new Fixture();
        await Canceled(() => canceled.Call(operation, json, cts.Token));
        Check(canceled.Backend.Calls.Count == 0, "Pre-canceled call accessed provider");
    }
    f = new Fixture();
    f.Backend.OnCall = _ => throw new InvalidOperationException("secret-token");
    var error = await f.Call("list_devices", "{}");
    Check(error.IsError && !error.Text.Contains("secret-token"), "Unexpected error leaked");
    Console.WriteLine("PASS cancellation, no mutation after canceled search, sanitized failures");
}

{
    var f = new Fixture();
    await f.Ducking.LowerVolumeAsync("speech", default);
    await f.Ducking.LowerVolumeAsync("transcription", default);
    await f.Ducking.RestoreVolumeAsync("speech", default);
    Check(f.Backend.Calls.Count(c => c.Name == "ChangeVolume") == 1, "Overlapping speech restored too early");
    await f.Ducking.RestoreVolumeAsync("transcription", default);
    Check((int)f.Backend.Calls.Last().Args[0]! == 80, "Volume not restored");
    await f.Ducking.LowerVolumeAsync("speech", default);
    await f.Ducking.ChangeVolumeAsync("set", 90, default);
    Check((int)f.Backend.Calls.Last().Args[0]! == 40, "Volume command bypassed active ducking");
    await f.Ducking.RestoreVolumeAsync("speech", default);
    Check((int)f.Backend.Calls.Last().Args[0]! == 90, "User volume command overwritten on restore");
    Console.WriteLine("PASS independent ducking, overlap and user volume changes");
}

{
    var f = new Fixture();
    var session = DispatchProxy.Create<IChatSessionChatAugmentationApi, SessionProxy>();
    var service = new SpotifyChatAugmentationsService(null!, null!, NullLoggerFactory.Instance);
    Check(service is IToolsChatAugmentationsService, "Missing tools marker");
    var plan = await service.PlanAsync(null!, default);
    var tools = plan.Single(p => p.Key == VoxtaModule.ToolsAugmentationKey);
    Check(tools.Activation == AugmentationActivation.OptIn && !tools.IncludeInCatchAll && tools.Tools!.Count == 17, "Tools plan not independent/opt-in");
    Check(await plan.Single(p => p.Key == VoxtaModule.AugmentationKey).ActivateAsync(session, default) == null, "Legacy session activated alongside tools");
    var monitor = new SpotifyPlaybackMonitor(f.Manager, session, NullLogger<SpotifyPlaybackMonitor>.Instance);
    var playback = new SpotifyPlaybackSession(monitor, f.Ducking);
    IToolsChatAugmentation instance = new SpotifyToolsSession(f.Catalog, playback);
    Check(instance.GetRequiredServiceTypes().Length == 0 && instance is not IActionInferenceAugmentation, "Tools depend on action inference");
    await instance.CallToolAsync("spotify_skip_next", null, default);
    await instance.CallToolAsync("spotify_skip_previous", null, default);
    await playback.InitializeAsync(default);
    Check(((SessionProxy)session).Contexts.All(c => c.Name != "Spotify Last Command"), "Last command context retained");
    await instance.DisposeAsync();
    await instance.DisposeAsync();
    Console.WriteLine("PASS tools session, no ActionInference, legacy suppression, multiple calls and disposal");
}
await SpotifyConfigurationChecks.RunAsync();
await SpotifyContextChecks.RunAsync();
await SpotifyVoxtaOAuthChecks.RunAsync();
await SpotifyPlaybackChecks.RunAsync();
Console.WriteLine("All Spotify tool checks passed.");

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Canceled(Func<Task> run)
{
    try { await run(); }
    catch (OperationCanceledException) { return; }
    throw new Exception("Cancellation was swallowed");
}

sealed class Fixture
{
    public readonly ISpotifyManager Manager = DispatchProxy.Create<ISpotifyManager, BackendProxy>();
    public BackendProxy Backend => (BackendProxy)Manager;
    public readonly Dictionary<string, string> Playlists = new() { ["daily mix 1"] = "special-id" };
    public SpotifySpeechDucking Ducking { get; }
    public SpotifyToolHandler Handler { get; }
    public ModuleToolCatalog Catalog { get; }
    public Fixture()
    {
        Ducking = new(Manager, () => Backend.State, 40);
        Handler = new(Manager, new SpotifySearchService(Manager, NullLogger<SpotifySearchService>.Instance), Playlists, Ducking);
        Catalog = SpotifyToolCatalog.Create(Handler, new(SpotifyToolCatalog.Choices.Select(c => c.Name).ToHashSet(), [ChatStyles.Assistant]), Playlists, NullLogger.Instance);
    }
    public Task<ChatToolCallResult> Call(string operation, string json, CancellationToken token = default) =>
        Catalog.CallToolAsync("spotify_" + operation, JsonSerializer.SerializeToElement(JsonSerializer.Deserialize<JsonElement>(json)), token).AsTask();
}

public class SessionProxy : DispatchProxy
{
    public ContextDefinition[] Contexts = [];
    public int ContextWrites;
    public int FlagWrites;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "IsAugmentationEnabled" => (string)args![0]! == VoxtaModule.ToolsAugmentationKey,
        "IsAugmentationSuppressed" => false,
        "SetFlags" => SaveFlags(),
        "SetContexts" => SaveContexts((ContextDefinition[])args![1]!),
        _ => throw new Exception("Unexpected session feedback/access: " + method.Name),
    };
    private Task SaveContexts(ContextDefinition[] contexts) { Contexts = contexts; ContextWrites++; return Task.CompletedTask; }
    private Task SaveFlags() { FlagWrites++; return Task.CompletedTask; }
}

public class BackendProxy : DispatchProxy
{
    public readonly List<(string Name, object?[] Args)> Calls = [];
    public bool Succeed = true;
    public bool FailureSeen;
    public string? Error;
    public bool AuthorizationRequired;
    public Action<string>? OnCall;
    public IReadOnlyList<Device> Devices = [new() { Name = "Desktop", Id = "device-id", Type = "Computer", IsActive = true }];
    public CurrentlyPlayingContext? State = new()
    {
        Device = new Device { Id = "device-id", Name = "Desktop", VolumePercent = 80, IsActive = true },
        IsPlaying = true, ProgressMs = 20000,
        Item = Track(),
    };
    private static FullTrack Track() => new() { Uri = "spotify:track:track-id", Id = "track-id", Name = "Track", DurationMs = 180000, Artists = [new SimpleArtist { Name = "Artist" }], Album = new SimpleAlbum { Name = "Album" } };
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var name = method!.Name;
        if (name == "get_LastUserVisibleError") return Error ?? (FailureSeen ? "Spotify provider failure." : null);
        if (name == "get_IsAuthorizationRequired") return AuthorizationRequired;
        Calls.Add((name, args ?? []));
        OnCall?.Invoke(name);
        foreach (var token in (args ?? []).OfType<CancellationToken>()) token.ThrowIfCancellationRequested();
        if (method.ReturnType == typeof(Task<bool>) && !Succeed) FailureSeen = true;
        return name switch
        {
            "GetCurrentPlaybackState" => Task.FromResult(State),
            "GetUsersTopTracks" => Task.FromResult<Paging<FullTrack>?>(new() { Items = [Track()] }),
            "SearchSpotify" => Task.FromResult<SearchResponse?>(new() { Tracks = new Paging<FullTrack, SearchResponse> { Items = [Track()] } }),
            "GetSpotifyUserIdAsync" => Task.FromResult<string?>("user-id"),
            "ListAvailablePlaylists" => Task.FromResult(new Dictionary<string, string> { ["Favorites"] = "spotify:playlist:playlist-id" }),
            "GetAvailablePlaybackDevices" => Task.FromResult(Devices),
            _ when method.ReturnType == typeof(Task<bool>) => Task.FromResult(Succeed),
            _ => throw new Exception("Unexpected provider call: " + name),
        };
    }
}
