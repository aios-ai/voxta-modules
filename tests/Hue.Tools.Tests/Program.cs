using System.Reflection;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Model.Shared.Forms;
using Voxta.Model.Shared;
using System.Text.Json;
using HueApi;
using HueApi.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Modules.Aios.PhilipsHue;
using Voxta.Modules.Aios.PhilipsHue.Clients;
using Voxta.Modules.Aios.PhilipsHue.Tools;
using Voxta.Modules.Aios.PhilipsHue.ChatAugmentations;
using Voxta.Modules.Aios.Tools;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
var data = new Data();
var connection = new Connection();
var commands = new Commands();
var manager = new HueManager(connection, data, commands, new HueEntityMatchingService(data, NullLogger<HueEntityMatchingService>.Instance), new ColorConverterService(NullLogger<ColorConverterService>.Instance), NullLogger<HueManager>.Instance);
await manager.InitializeAsync(default);
var settings = new HueToolsSettings(HueToolCatalog.Choices.Select(c => c.Name).ToHashSet(), [Voxta.Model.Shared.ChatStyles.Assistant]);
var catalog = HueToolCatalog.Create(new HueToolHandler(manager, "Desk"), settings, "Desk", NullLogger.Instance);
async Task<ChatToolCallResult> Call(string name, string json = "{}", CancellationToken token = default) => await catalog.CallToolAsync("hue_" + name, JsonSerializer.Deserialize<JsonElement>(json), token);
foreach (var (name, json) in new[] {
    ("bridge_connect", "{}"), ("show_hue_inventory", "{}"),
    ("turn_lights_on", "{}"), ("turn_lights_off", "{\"target\":\"Desk\"}"),
    ("change_color", "{\"target\":\"Desk\",\"color\":\"Red\"}"),
    ("show_emotion", "{\"color\":\"Blue\"}"),
    ("change_brightness", "{\"target\":\"Desk\",\"brightness\":50}"),
    ("activate_scene", "{\"target\":\"Office\",\"scene\":\"Relax\"}") })
{
    Check(!(await Call(name, json)).IsError, name);
    Console.WriteLine("PASS " + name);
}
Check(commands.LastId == data.Scenes[0].Id && commands.LastScene == "Relax", "Scene resource must use the scene ID");
var before = commands.Count;
foreach (var (name, json) in new[] {
    ("turn_lights_off", "{\"target\":\"Unknown\"}"), ("turn_lights_on", "{\"target\":null}"),
    ("change_brightness", "{\"target\":\"Desk\",\"brightness\":101}"),
    ("change_brightness", "{\"target\":\"Desk\",\"brightness\":\"20\"}"),
    ("change_color", "{\"target\":\"Desk\",\"color\":\"not-a-color\"}"),
    ("activate_scene", "{\"target\":\"Desk\",\"scene\":\"Relax\"}") })
    Check((await Call(name, json)).IsError, "Invalid input accepted: " + json);
Check(commands.Count == before, "Invalid inputs caused mutations");
data.Lights.Add(JsonSerializer.Deserialize<Light>("{\"id\":\"" + Guid.NewGuid() + "\",\"metadata\":{\"name\":\"Desk\"}}")!);
Check((await Call("turn_lights_on", "{\"target\":\"Desk\"}")).IsError && commands.Count == before, "Ambiguous target mutated");
data.Lights.RemoveAt(1);
commands.Pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var inFlight = Call("turn_lights_on");
Check(commands.Count == before + 1, "First command not dispatched");
using (var waiting = new CancellationTokenSource())
{
    var queued = Call("turn_lights_off", "{}", waiting.Token);
    waiting.Cancel();
    try { await queued; throw new Exception("Queued cancellation swallowed"); } catch (OperationCanceledException) { }
    Check(commands.Count == before + 1, "Concurrent call bypassed gate");
}
commands.Pending.SetResult(true);
await inFlight;
commands.Pending = null;
before = commands.Count;
commands.Success = false;
Check((await Call("turn_lights_on")).IsError && commands.Count == before + 1, "Failure retried or hidden");
connection.Connected = false;
before = commands.Count;
Check((await Call("turn_lights_on")).IsError && commands.Count == before, "Disconnected mutation");
using var cts = new CancellationTokenSource(); cts.Cancel();
try { await Call("turn_lights_on", "{}", cts.Token); throw new Exception("Cancellation swallowed"); } catch (OperationCanceledException) { }
var definitions = catalog.GetTools();
Check(definitions.Count == 8 && definitions.All(t => t.Source == VoxtaModule.ServiceName && t.AugmentationKey == VoxtaModule.ToolsAugmentationKey && !t.AlwaysAllowed), "Metadata");
Check(definitions.Single(t => t.Name == "hue_show_hue_inventory").Permission == ToolPermissionClass.Read, "Inventory permission");
Check(HueToolCatalog.Choices.Single(t => t.Name == "hue_show_emotion").EnabledByDefault == false, "Emotion default");
Check(!HueToolCatalog.Create(new HueToolHandler(manager, null), settings, null, NullLogger.Instance).GetTools().Single(t => t.Name == "hue_show_emotion").Enabled, "Emotion target gate");
var disabled = HueToolCatalog.Create(new HueToolHandler(manager, null), new HueToolsSettings(new HashSet<string>(), []), null, NullLogger.Instance);
Check((await disabled.CallToolAsync("hue_turn_lights_on", null, default)).IsError, "Disabled tool executed");
IToolsChatAugmentation adapter = new ModuleToolsAugmentation(catalog);
Check(adapter.GetRequiredServiceTypes().Length == 0 && adapter is not IActionInferenceAugmentation, "Action inference dependency");
var service = new PhilipsHueChatAugmentationsService(NullLoggerFactory.Instance);
Check(service is IToolsChatAugmentationsService, "Tools marker");
var plans = await service.PlanAsync(null!, default);
Check(plans.Length == 2 && plans.Single(p => p.Key == "hue_tools") is { IncludeInCatchAll: false, Activation: AugmentationActivation.OptIn }, "Independent tools plan");
var defaults = HueToolsSettings.From(new StaticSettingsSource(new() { ["Actions"] = "invalid legacy actions" }));
Check(defaults.Selected.Count == 7 && defaults.ChatStyles.ToHashSet().SetEquals([ChatStyles.Assistant, ChatStyles.Companion]), "Independent defaults");
foreach (var flag in new[] { "true", "\"true\"" })
{
    var saved = HueToolsSettings.From(new StaticSettingsSource(new() { ["HueTools"] = "[{\"name\":\"hue_turn_lights_on\",\"disabled\":" + flag + ",\"description\":\"Custom turn on\"}]" }));
    Check(!saved.Selected.Contains("hue_turn_lights_on") && saved.Descriptions!["hue_turn_lights_on"] == "Custom turn on", "Card disabled/description parsing");
}
Check(((FormArrayOfObjectsField)HueToolsSettings.ConfigurationField(new StaticSettingsSource(new()))).AllowDisable, "Card editor");
var session = DispatchProxy.Create<IChatSessionChatAugmentationApi, SessionProxy>();
Check(await plans.Single(p => p.Key == "hue").ActivateAsync(session, default) == null, "Both selections created legacy instance");
await new HueUserInteractionWrapper(session, false).SetBridgeConnectionStateAsync(true, default);
Console.WriteLine("PASS settings, actions independence, duplicate suppression, no legacy flags");
Console.WriteLine("PASS validation, failure/no retry, cancellation, permissions, emotion gate, tools-only SDK registration");
Console.WriteLine("SDK tools contract: " + typeof(IToolsChatAugmentation).Assembly.GetName().Version);
foreach (var method in typeof(IToolsChatAugmentation).GetMethods()) Console.WriteLine(method);

sealed class Connection : IHueBridgeConnectionService
{
    public bool Connected = true;
    public LocalHueApi? HueClient => null;
    public bool IsConnected => Connected;
    public bool IsAuthorizationRequired => false;
    public HueBridgeState State => Connected ? HueBridgeState.Connected : HueBridgeState.Disconnected;
    public string? LastUserVisibleError => null;
    public Task<bool> InitializeBridgeAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Connected); }
}
sealed class Commands : IHueCommandService
{
    public TaskCompletionSource<bool>? Pending;
    public int Count; public bool Success = true; public Guid LastId; public string? LastScene;
    public string? LastUserVisibleError => Success ? null : "Bridge rejected command.";
    public Task<bool> SendHueCommandAsync(Guid id, string type, bool? state = null, string? color = null, double? brightness = null, string? scene = null, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Count++; LastId = id; LastScene = scene; return Task.FromResult(Success); }
    public Task<bool> ControlAllLightsAsync(bool on, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Count++; return Pending?.Task ?? Task.FromResult(Success); }
}
sealed class Data : IHueDataService
{
    public IList<Light> Lights { get; } = [JsonSerializer.Deserialize<Light>("{\"id\":\"11111111-1111-1111-1111-111111111111\",\"metadata\":{\"name\":\"Desk\"}}")!];
    public IList<GroupedLight> Groups { get; } = [];
    public IList<Room> Rooms { get; } = [JsonSerializer.Deserialize<Room>("{\"id\":\"22222222-2222-2222-2222-222222222222\",\"metadata\":{\"name\":\"Office\"},\"services\":[{\"rid\":\"33333333-3333-3333-3333-333333333333\",\"rtype\":\"grouped_light\"}]}")!];
    public IList<Zone> Zones { get; } = [];
    public IList<Scene> Scenes { get; } = [JsonSerializer.Deserialize<Scene>("{\"id\":\"44444444-4444-4444-4444-444444444444\",\"metadata\":{\"name\":\"Relax\"},\"group\":{\"rid\":\"22222222-2222-2222-2222-222222222222\",\"rtype\":\"room\"}}")!];
    public string? LastUserVisibleError => null;
    public Task<bool> RetrieveBridgeDataAsync() => Task.FromResult(true);
}

public class SessionProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "IsAugmentationEnabled" => (string)args![0]! == "hue_tools",
        "IsAugmentationSuppressed" => false,
        _ => throw new Exception("Unexpected session feedback: " + method.Name),
    };
}
