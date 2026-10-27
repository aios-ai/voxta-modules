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
    ("search", "{\"query\":\"Track\",\"type\":\"track\"}", null),
    ("play_random_music", "{}", "PlayWithResult"),
    ("play_special_playlist", "{\"name\":\"Daily Mix 1\"}", "PlayWithResult"),
    ("play_music", "{\"selection\":\"spotify:track:1234567890123456789012\"}", "PlayWithResult"),
    ("queue_track", "{\"selection\":\"spotify:track:1234567890123456789012\"}", "QueueTrack"),
    ("volume", "{\"type\":\"set\",\"value\":30}", "ChangeVolume"),
    ("seek_playback", "{\"target\":\"forward\"}", "SeekPlayback"),
    ("skip", "{\"direction\":\"next\"}", "SkipToPreviousOrNextTrack"),
    ("skip", "{\"direction\":\"previous\"}", "SkipToPreviousOrNextTrack"),
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
        if (operation == "skip")
            Check((string)fixture.Backend.Calls.Single(c => c.Name == mutation).Args[0]! == JsonDocument.Parse(json).RootElement.GetProperty("direction").GetString(), "Wrong skip direction sent");
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
    ("search", "{\"query\":\"Track\",\"type\":\"genre\"}"),
    ("search", "{\"query\":\" \",\"type\":\"track\"}"),
    ("play_music", "{\"selection\":\"https://example.com\"}"),
    ("play_music", "{\"selection\":\"spotify:track:123456789012345678901!\"}"),
    ("queue_track", "{\"selection\":\"spotify:artist:1234567890123456789012\"}"),
    ("play_music", "{\"selection\":\"Intro by Limp Bizkit\",\"uri\":\"spotify:track:1234567890123456789012\"}"),
    ("play_music", "{\"uri\":\"spotify:track:1234567890123456789012\"}"),
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
    ("skip", "{}"),
    ("skip", "{\"direction\":\"forward\"}"),
    ("skip", "{\"direction\":2}"),
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
    f.Backend.SearchResults = new()
    {
        Tracks = new() { Items = [
            new() { Uri = "spotify:track:1111111111111111111111", Name = "First", Artists = [new() { Name = "Limp Bizkit" }], Album = new() { Name = "Album One", ReleaseDate = "2000-10-17", ReleaseDatePrecision = "day" } },
            new() { Uri = "spotify:track:2222222222222222222222", Name = "Second", Artists = [new() { Name = "Other artist" }], Album = new() { Name = "Album Two", ReleaseDate = "1999", ReleaseDatePrecision = "year" } },
        ] },
    };
    var query = "artist:\"Limp Bizkit\"";
    var result = await f.Call("search", JsonSerializer.Serialize(new { query, type = "track" }));
    using var document = JsonDocument.Parse(result.Text);
    var candidates = document.RootElement.GetProperty("results");
    Check(!result.IsError && candidates.GetArrayLength() == 2, "Search did not return candidates");
    Check(candidates[0].GetProperty("Name").GetString() == "First" && candidates[1].GetProperty("Name").GetString() == "Second", "Search reordered or selected candidates");
    Check(candidates[0].GetProperty("Artists")[0].GetString() == "Limp Bizkit" && candidates[0].GetProperty("Album").GetString() == "Album One", "Disambiguating metadata missing");
    var call = f.Backend.Calls.Single();
    Check(call.Name == "SearchSpotify" && (string)call.Args[0]! == query && (SearchRequest.Types)call.Args[1]! == SearchRequest.Types.Track && call.Args[2] == null, "Search filters, type or user market changed");
    var chosen = candidates[1].GetProperty("Selection").GetString();
    Check(chosen!.Contains("Second") && chosen.Contains("Other artist") && chosen.Contains("Album Two") && chosen.Contains("1999") && !chosen.Contains("spotify:"), "Approval selection is not human readable");
    Check(!result.Text.Contains("spotify:") && !candidates[0].TryGetProperty("Uri", out _), "Internal URI leaked into search selection");
    Check(candidates[0].GetProperty("ReleaseDate").GetString() == "2000-10-17" && candidates[0].GetProperty("ReleaseDatePrecision").GetString() == "day", "Track album release date/precision missing");
    Check(candidates[1].GetProperty("ReleaseDate").GetString() == "1999" && candidates[1].GetProperty("ReleaseDatePrecision").GetString() == "year", "Year-only date expanded or lost");
    await f.Call("play_music", JsonSerializer.Serialize(new { selection = chosen }));
    Check((string)f.Backend.Calls.Single(c => c.Name == "PlayWithResult").Args[0]! == "spotify:track:2222222222222222222222" && f.Backend.Calls.Count(c => c.Name == "SearchSpotify") == 1, "Agent selection ignored or playback searched again");
    await f.Call("queue_track", JsonSerializer.Serialize(new { selection = chosen }));
    Check((string)f.Backend.Calls.Single(c => c.Name == "QueueTrack").Args[0]! == "spotify:track:2222222222222222222222", "Queue did not privately resolve approved track");
    var otherChat = new Fixture();
    Check((await otherChat.Call("play_music", JsonSerializer.Serialize(new { selection = chosen }))).IsError && otherChat.Backend.Calls.Count == 0, "Selection leaked between chat sessions");
    f.Backend.SearchResults = new()
    {
        Albums = new() { Items = [new() { Uri = "spotify:album:album", Name = "Album", Artists = [new() { Name = "Artist" }], ReleaseDate = "2024-08", ReleaseDatePrecision = "month" }] },
        Artists = new() { Items = [new() { Uri = "spotify:artist:artist", Name = "Artist" }] },
        Playlists = new() { Items = [null!, new() { Uri = "spotify:playlist:playlist", Name = "Playlist", Owner = new() { DisplayName = "Owner" } }] },
        Shows = new() { Items = [new() { Uri = "spotify:show:show", Name = "Show", Description = "Show description" }] },
        Episodes = new() { Items = [new() { Uri = "spotify:episode:episode", Name = "Episode", ReleaseDate = "2026-10-26", ReleaseDatePrecision = "day" }] },
    };
    foreach (var type in new[] { "album", "artist", "playlist", "show", "episode" })
    {
        var response = await f.Call("search", JsonSerializer.Serialize(new { query = "example", type }));
        using var media = JsonDocument.Parse(response.Text);
        var items = media.RootElement.GetProperty("results");
        Check(!response.IsError && items.GetArrayLength() == 1 && items[0].GetProperty("Selection").GetString()!.StartsWith(type + ": "), "Wrong media type or null candidate handling: " + type);
        if (type == "album") Check(items[0].GetProperty("ReleaseDate").GetString() == "2024-08" && items[0].GetProperty("ReleaseDatePrecision").GetString() == "month", "Album month precision lost");
        if (type == "episode") Check(items[0].GetProperty("ReleaseDate").GetString() == "2026-10-26" && items[0].GetProperty("ReleaseDatePrecision").GetString() == "day", "Episode date/precision lost");
        if (type == "artist") Check(!items[0].TryGetProperty("ReleaseDate", out _), "Artist release date fabricated");
    }
    f.Backend.PlaybackResult = new(true, false, f.Backend.State, "Playback state is delayed.");
    var unconfirmed = await f.Call("play_music", "{\"selection\":\"spotify:artist:1234567890123456789012\"}");
    Check(!unconfirmed.IsError && unconfirmed.Text.Contains("accepted_unconfirmed") && unconfirmed.Text.Contains("Track by Artist"), "Acknowledged command reported as failed or observed item missing");
    f.Backend.SearchResults = new();
    using var empty = JsonDocument.Parse((await f.Call("search", "{\"query\":\"missing\",\"type\":\"artist\"}")).Text);
    Check(empty.RootElement.GetProperty("results").GetArrayLength() == 0, "Empty results not returned as data");
    f.Backend.SearchResults = null;
    f.Backend.Error = "Search authorization failed.";
    var failedSearch = await f.Call("search", "{\"query\":\"missing\",\"type\":\"artist\"}");
    Check(failedSearch.IsError && failedSearch.Text == f.Backend.Error, "Search failure hidden as empty results");
    Console.WriteLine("PASS search ordering/metadata, explicit agent selection, no hidden playback/search, accepted results and search errors");
}

{
    var selections = new SpotifyToolSelections();
    var first = selections.Remember(new("spotify:track:1111111111111111111111", "track", "Intro", ["Limp Bizkit"], "Album", "2000", "year"));
    var duplicate = selections.Remember(first with { Uri = "spotify:track:2222222222222222222222" });
    Check(first.Selection != duplicate.Selection && selections.Resolve(first.Selection)?.Uri == first.Uri && selections.Resolve(duplicate.Selection)?.Uri == duplicate.Uri, "Identical titles rebound to another URI");
    Check(selections.Remember(first).Selection == first.Selection, "Repeated search changed a retained selection");
    for (var i = 0; i < 200; i++) selections.Remember(new($"spotify:track:{i:D22}", "track", "Intro"));
    Check(selections.Resolve(first.Selection) == null, "Old selection cache was not bounded");
    var refreshed = selections.Remember(first);
    Check(refreshed.Selection != first.Selection && selections.Resolve(first.Selection) == null, "Expired approval label was reused");
    Console.WriteLine("PASS readable selections bind exact URIs, distinguish duplicate titles and never rebind expired labels");
}

{
    var f = new Fixture();
    var definitions = f.Catalog.GetTools();
    Check(definitions.Count == 16 && definitions.Select(d => d.Name).Distinct().Count() == 16, "Catalog coverage");
    Check(definitions.All(d => d.Name is not ("spotify_skip_next" or "spotify_skip_previous")), "Old skip tools retained");
    var skipSchema = definitions.Single(d => d.Name == "spotify_skip").InputSchema;
    Check(skipSchema.GetProperty("required").EnumerateArray().Any(p => p.GetString() == "direction"), "Skip direction is not required");
    Check((await f.Catalog.CallToolAsync("spotify_skip_next", null, default)).IsError
        && (await f.Catalog.CallToolAsync("spotify_skip_previous", null, default)).IsError, "Removed skip tools executed");
    Check(definitions.All(d => d.Source == VoxtaModule.ServiceName && d.AugmentationKey == VoxtaModule.ToolsAugmentationKey && !d.AlwaysAllowed && d.ExternalContent), "Tool ownership/permissions");
    Check(definitions.Count(d => d.Permission == ToolPermissionClass.Read) == 3, "Read/Act classification");
    foreach (var name in new[] { "spotify_play_music", "spotify_queue_track" })
    {
        var properties = definitions.Single(d => d.Name == name).InputSchema.GetProperty("properties");
        Check(properties.TryGetProperty("selection", out _) && !properties.TryGetProperty("uri", out _), "Approval schema exposes URI instead of readable selection");
    }
    Check(SpotifyToolCatalog.Choices.Where(c => c.EnabledByDefault).Count() == 14 && SpotifyToolCatalog.Choices.Where(c => !c.EnabledByDefault).Select(c => c.Name).ToHashSet().SetEquals(["spotify_list_devices", "spotify_transfer_to_device"]), "Device tools must default off");
    var settings = new SpotifyToolsSettings(new HashSet<string>(), [ChatStyles.Assistant]);
    var disabled = SpotifyToolCatalog.Create(f.Handler, settings, f.Playlists, NullLogger.Instance);
    Check(disabled.GetTools().All(t => !t.Enabled), "Disabled choices not preserved");
    Check((await disabled.CallToolAsync("spotify_skip", JsonSerializer.SerializeToElement(new { direction = "next" }), default)).IsError && f.Backend.Calls.Count == 0, "Disabled tool executed");
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
    foreach (var operation in new[] { "list_devices" })
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
    await Canceled(() => f.Call("search", "{\"query\":\"Track\",\"type\":\"track\"}", cts.Token));
    Check(!f.Backend.Calls.Any(c => c.Name == "PlayWithResult"), "Canceled search started playback");
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
    Check(tools.Activation == AugmentationActivation.OptIn && !tools.IncludeInCatchAll && tools.Tools!.Count == 16, "Tools plan not independent/opt-in");
    Check(plan.Length == 1 && VoxtaModule.Augmentations.Length == 1, "Legacy augmentation still registered");
    Check((await f.Catalog.CallToolAsync("spotify_connect", null, default)).IsError, "Removed connect tool executed");
    var monitor = new SpotifyPlaybackMonitor(f.Manager, session, NullLogger<SpotifyPlaybackMonitor>.Instance);
    var playback = new SpotifyPlaybackSession(monitor, f.Ducking);
    IToolsChatAugmentation instance = new SpotifyToolsSession(f.Catalog, playback);
    Check(instance.GetRequiredServiceTypes().Length == 0 && instance is not IActionInferenceAugmentation, "Tools depend on action inference");
    await instance.CallToolAsync("spotify_skip", JsonSerializer.SerializeToElement(new { direction = "next" }), default);
    await instance.CallToolAsync("spotify_skip", JsonSerializer.SerializeToElement(new { direction = "previous" }), default);
    await playback.InitializeAsync(default);
    Check(((SessionProxy)session).Contexts.All(c => c.Name != "Spotify Last Command"), "Last command context retained");
    await instance.DisposeAsync();
    await instance.DisposeAsync();
    Console.WriteLine("PASS tools session, no ActionInference, tools-only registration, multiple calls and disposal");
}
{
    var f = new Fixture();
    f.Backend.LibraryResult = SpotifyLibrarySaveResult.AlreadySaved;
    var result = await f.Call("add_to_favorites", "{}");
    Check(!result.IsError && result.Text.Contains("already in Liked Songs") && !result.Text.Contains("Saved "), "Already liked track reported as newly saved");
    Check(f.Backend.Calls.Count(c => c.Name == "AddTrackToLibraryAsync") == 1, "Favorites result retried");
}
await SpotifyDuckingChecks.RunAsync();
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
        Handler = new(Manager, Playlists, Ducking);
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
    public SpotifyPlaybackResult? PlaybackResult;
    public SpotifyLibrarySaveResult? LibraryResult;
    public SearchResponse? SearchResults = new() { Tracks = new() { Items = [Track()] } };
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
        if ((method.ReturnType == typeof(Task<bool>) || name == "AddTrackToLibraryAsync") && !Succeed) FailureSeen = true;
        return name switch
        {
            "AddTrackToLibraryAsync" => Task.FromResult(LibraryResult ?? (Succeed ? SpotifyLibrarySaveResult.Saved : SpotifyLibrarySaveResult.Failed)),
            "PlayWithResult" => Task.FromResult(PlaybackResult ?? new SpotifyPlaybackResult(Succeed, Succeed, State, Succeed ? null : Error ?? "Spotify provider failure.")),
            "GetCurrentPlaybackState" => Task.FromResult(State),
            "GetUsersTopTracks" => Task.FromResult<Paging<FullTrack>?>(new() { Items = [Track()] }),
            "SearchSpotify" => Task.FromResult(SearchResults),
            "GetSpotifyUserIdAsync" => Task.FromResult<string?>("user-id"),
            "ListAvailablePlaylists" => Task.FromResult(new Dictionary<string, string> { ["Favorites"] = "spotify:playlist:playlist-id" }),
            "GetAvailablePlaybackDevices" => Task.FromResult(Devices),
            _ when method.ReturnType == typeof(Task<bool>) => Task.FromResult(Succeed),
            _ => throw new Exception("Unexpected provider call: " + name),
        };
    }
}
