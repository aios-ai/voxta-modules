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
using Voxta.Abstractions.Utils;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
var data = new Data();
data.Groups.Add(JsonSerializer.Deserialize<GroupedLight>("""{"id":"33333333-3333-3333-3333-333333333333","metadata":{"name":"Office lights"}}""")!);
data.Groups.Add(JsonSerializer.Deserialize<GroupedLight>("""{"id":"55555555-5555-5555-5555-555555555555","metadata":{"name":"Accent"}}""")!);
var connection = new Connection();
var commands = new Commands();
var manager = new HueManager(connection, data, commands, new ColorConverterService(NullLogger<ColorConverterService>.Instance), NullLogger<HueManager>.Instance);
await manager.InitializeAsync(default);
var settings = new HueToolsSettings(HueToolCatalog.Choices.Select(c => c.Name).ToHashSet(), [ChatStyles.Assistant]);
var catalog = HueToolCatalog.Create(new HueToolHandler(manager, "Desk"), settings, "Desk", NullLogger.Instance);
async Task<ChatToolCallResult> Call(string name, string json = "{}", CancellationToken token = default) => await catalog.CallToolAsync("hue_" + name, JsonSerializer.Deserialize<JsonElement>(json), token);
foreach (var (name, json) in new[] {
    ("toggle_lights", """{"state":"on"}"""),
    ("toggle_lights", """{"state":"off","target":"Desk"}"""),
    ("show_hue_inventory", "{}"),
    ("change_color", """{"target":"Desk","color":"Red"}"""),
    ("show_emotion", """{"color":"Blue"}"""),
    ("change_brightness", """{"target":"Desk","brightness":50}"""),
    ("activate_scene", """{"target":"Office","scene":"Relax"}""") })
{
    Check(!(await Call(name, json)).IsError, name);
    if (name == "toggle_lights") Check(commands.LastState == json.Contains("on"), "Wrong on/off command");
    Console.WriteLine("PASS " + name);
}
Check(commands.LastId == data.Scenes[0].Id && commands.LastScene == "Relax", "Scene resource ID");
var before = commands.Count;
foreach (var (name, json) in new[] {
    ("toggle_lights", "{}"), ("toggle_lights", """{"state":true}"""),
    ("toggle_lights", """{"state":"toggle"}"""), ("toggle_lights", """{"state":"off","target":"Unknown"}"""),
    ("toggle_lights", """{"state":"on","target":null}"""),
    ("change_brightness", """{"target":"Desk","brightness":101}"""),
    ("change_brightness", """{"target":"Desk","brightness":"20"}"""),
    ("change_color", """{"target":"Desk","color":"not-a-color"}"""),
    ("activate_scene", """{"target":"Desk","scene":"Relax"}""") })
    Check((await Call(name, json)).IsError, "Invalid input accepted: " + json);
Check(commands.Count == before, "Invalid inputs caused mutations");
data.Lights.Add(JsonSerializer.Deserialize<Light>("{\"id\":\"" + Guid.NewGuid() + "\",\"metadata\":{\"name\":\"Desk\"}}")!);
Check((await Call("toggle_lights", """{"state":"on","target":"Desk"}""")).IsError && commands.Count == before, "Ambiguous target mutated");
data.Lights.RemoveAt(1);
commands.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
var inFlight = Call("toggle_lights", """{"state":"on"}""");
Check(commands.Count == before + 1, "First command not dispatched");
using (var waiting = new CancellationTokenSource())
{
    var queued = Call("toggle_lights", """{"state":"off"}""", waiting.Token);
    waiting.Cancel();
    try { await queued; throw new Exception("Queued cancellation swallowed"); } catch (OperationCanceledException) { }
    Check(commands.Count == before + 1, "Concurrent call bypassed gate");
}
commands.Pending.SetResult(true);
await inFlight;
commands.Pending = null;
before = commands.Count;
commands.Success = false;
Check((await Call("toggle_lights", """{"state":"on"}""")).IsError && commands.Count == before + 1, "Failure retried or hidden");
commands.Success = true;
var definitions = catalog.GetTools();

// Each card must show only its own schema, including when upgrading saved cards.
var argumentField = (FormArrayOfObjectsField)HueToolsSettings.ConfigurationField(new StaticSettingsSource(new()));
var argumentCards = JsonSerializer.Deserialize<HueToolSettings[]>(argumentField.DefaultValue!)!;
Check(argumentField.FieldTemplate.OfType<FormMultilineField>().Any(f => f.Name == "Arguments"), "Missing Arguments inside tool card");
var argumentLock = argumentField.FieldTemplate.OfType<FormBooleanField>().Single(f => f.Name == "ArgumentsReadOnly");
Check(argumentLock.Advanced && argumentLock.DefaultValue == true && argumentLock.DisablesFieldsOnActive!.Contains("Arguments"), "Argument reference must be read-only");
var savedArgumentCards = new StaticSettingsSource(new()
{
    [HueToolsSettings.CardsFieldName] = JsonSerializer.Serialize(new[]
    {
        new HueToolSettings { Name = "hue_toggle_lights", Description = "Saved card description", Disabled = "true", Arguments = "Not agent instructions" },
    }),
});
var savedArgumentSettings = HueToolsSettings.From(savedArgumentCards);
Check(!savedArgumentSettings.Selected.Contains("hue_toggle_lights") && savedArgumentSettings.Descriptions!["hue_toggle_lights"] == "Saved card description", "New card settings lost");
var refreshedArgumentField = (FormArrayOfObjectsField)HueToolsSettings.ConfigurationField(savedArgumentCards);
var refreshedCard = JsonSerializer.Deserialize<HueToolSettings[]>(refreshedArgumentField.DefaultValue!)!.Single(c => c.Name == "hue_toggle_lights");
Check(refreshedCard.Arguments == HueToolCatalog.DescribeArguments("hue_toggle_lights"), "Saved argument text must not configure the agent");
foreach (var tool in catalog.GetTools())
{
    var argumentHelp = argumentCards.Single(c => c.Name == tool.Name).Arguments!;
    var required = tool.InputSchema.TryGetProperty("required", out var requiredNames)
        ? requiredNames.EnumerateArray().Select(n => n.GetString()).ToHashSet() : [];
    var properties = tool.InputSchema.GetProperty("properties").EnumerateObject().ToArray();
    if (properties.Length == 0) Check(argumentHelp == "No arguments.", "No-argument tools must be explicit");
    foreach (var property in properties)
    {
        var expected = property.Name + " (" + property.Value.GetProperty("type").GetString()
            + ", " + (required.Contains(property.Name) ? "required" : "optional") + "): "
            + property.Value.GetProperty("description").GetString();
        Check(argumentHelp.Contains(expected), "Incomplete argument help for " + tool.Name + "." + property.Name);
    }
    Check(argumentHelp == HueToolCatalog.DescribeArguments(tool.Name), "Wrong tool's arguments displayed");
}

Check(definitions.Count == 6 && definitions.All(t => t.Source == VoxtaModule.ServiceName && t.AugmentationKey == VoxtaModule.ToolsAugmentationKey && !t.AlwaysAllowed), "Metadata");
foreach (var removed in new[] { "bridge_connect", "turn_lights_on", "turn_lights_off" })
    Check((await Call(removed)).IsError, "Removed tool dispatched");
Check(definitions.Single(t => t.Name == "hue_show_hue_inventory").Permission == ToolPermissionClass.Read, "Inventory permission");
Check(HueToolCatalog.Choices.Single(t => t.Name == "hue_show_emotion").EnabledByDefault == false, "Emotion default");
Check(!HueToolCatalog.Create(new HueToolHandler(manager, null), settings, null, NullLogger.Instance).GetTools().Single(t => t.Name == "hue_show_emotion").Enabled, "Emotion target gate");
var defaults = HueToolsSettings.From(new StaticSettingsSource(new()));
Check(defaults.Selected.Count == 5 && defaults.ChatStyles.ToHashSet().SetEquals([ChatStyles.Assistant, ChatStyles.Companion]), "Independent defaults");
foreach (var flag in new[] { "true", "\"true\"" })
{
    var saved = HueToolsSettings.From(new StaticSettingsSource(new() { ["HueToolCards"] = "[{\"name\":\"hue_toggle_lights\",\"disabled\":" + flag + ",\"description\":\"Custom toggle\"}]" }));
    Check(!saved.Selected.Contains("hue_toggle_lights") && saved.Descriptions!["hue_toggle_lights"] == "Custom toggle", "Card parsing");
}
var cards = (FormArrayOfObjectsField)HueToolsSettings.ConfigurationField(new StaticSettingsSource(new()));
Check(cards.Name == HueToolsSettings.CardsFieldName && JsonSerializer.Deserialize<HueToolSettings[]>(cards.DefaultValue!)!.Length == 6, "Current tool editor");
var configurationFields = await new Voxta.Modules.Aios.PhilipsHue.Configuration.ModuleConfigurationProvider(null!, null!, null!, null!, null!, null!).GetModuleConfigurationFieldsAsync(null!, new StaticSettingsSource(new()), default);
Check(configurationFields.All(f => f is not FormDocumentationField), "Separate argument reference retained");
var session = DispatchProxy.Create<IChatSessionChatAugmentationApi, SessionProxy>();
var proxy = (SessionProxy)(object)session;
var defaultCatalog = HueToolCatalog.Create(new HueToolHandler(manager, "Desk"), defaults, "Desk", NullLogger.Instance);
IToolsChatAugmentation adapter = new HueToolsSession(defaultCatalog, manager, session);
Check(adapter.GetRequiredServiceTypes().Length == 0 && adapter is not IActionInferenceAugmentation, "Action inference dependency");
Check((await adapter.GetToolsAsync(default)).Count(t => t.Enabled) == 5, "Connected saved selection");
var basic = proxy.Contexts.Single(c => c.Name == "Philips Hue Basic Inventory");
Check(basic.Text.Contains("Desk") && basic.Text.Contains("Office") && !basic.Text.Contains("Relax") && basic.Text.Contains("Accent") && !basic.Text.Contains("Office lights"), "Basic names missing or scenes leaked into basic inventory");
var inventoryId = basic.Id;
Check(proxy.Contexts.All(c => c.Name != "Philips Hue Status"), "Connected status should be absent");
await adapter.GetToolsAsync(default);
Check(proxy.ContextWrites == 1, "Unchanged context republished");
connection.Connected = false;
Check((await adapter.GetToolsAsync(default)).All(t => !t.Enabled), "Disconnected tools offered");
Check(proxy.Contexts.Single(c => c.Name == "Philips Hue Status").Text.Contains("restart the chat"), "Restart context/stable identity");
Check(proxy.Contexts.Length == 1, "Disconnected context retained stale inventory");
before = commands.Count;
Check((await adapter.CallToolAsync("hue_toggle_lights", JsonSerializer.Deserialize<JsonElement>("""{"state":"on"}"""), default)).IsError && commands.Count == before, "Stale offered tool mutated");
connection.Connected = true;
await manager.InitializeAsync(default);
Check((await adapter.GetToolsAsync(default)).Count(t => t.Enabled) == 5, "Recovery enabled disabled tool");
Check(proxy.Contexts.Single(c => c.Name == "Philips Hue Basic Inventory").Id == inventoryId
    && proxy.Contexts.All(c => c.Name != "Philips Hue Status"), "Recovery retained failure status or changed inventory identity");
var inventoryOff = new HueToolsSettings(defaults.Selected.Where(n => n != "hue_show_hue_inventory").ToHashSet(), defaults.ChatStyles);
var otherSession = DispatchProxy.Create<IChatSessionChatAugmentationApi, SessionProxy>();
var otherAdapter = new HueToolsSession(HueToolCatalog.Create(new HueToolHandler(manager, null), inventoryOff, null, NullLogger.Instance), manager, otherSession);
Check(!(await otherAdapter.GetToolsAsync(default)).Single(t => t.Name == "hue_show_hue_inventory").Enabled
    && ((SessionProxy)(object)otherSession).Contexts.Any(c => c.Name == "Philips Hue Basic Inventory"), "Basic names depended on enabling inventory tool");
commands.IsConnectionUnavailable = true;
Check((await adapter.GetToolsAsync(default)).All(t => !t.Enabled), "Known transport loss left tools available");
commands.IsConnectionUnavailable = false;
data.Success = false;
Check(!await manager.InitializeAsync(default) && !manager.IsConnected, "Partial initialization enabled tools");
Check((await adapter.GetToolsAsync(default)).All(t => !t.Enabled), "Failed initialization offered tools");
using var cts = new CancellationTokenSource(); cts.Cancel();
try { await adapter.GetToolsAsync(cts.Token); throw new Exception("Cancellation swallowed"); } catch (OperationCanceledException) { }
var service = new PhilipsHueChatAugmentationsService(NullLoggerFactory.Instance, null!, null!);
Check(service is IToolsChatAugmentationsService, "Tools marker");
var plans = await service.PlanAsync(null!, default);
Check(plans.Length == 1 && VoxtaModule.Augmentations.Length == 1 && plans.Single() is { Key: "hue_tools", IncludeInCatchAll: false, Activation: AugmentationActivation.OptIn }, "Legacy plan remains");
var fields = await new Voxta.Modules.Aios.PhilipsHue.Configuration.ModuleConfigurationProvider(null!, null!, null!, null!, null!, null!).GetModuleConfigurationFieldsAsync(null!, new StaticSettingsSource(new()), default);
Check(fields.OfType<Voxta.Model.Shared.Forms.FormInvokeActionField>().Single().Name == "ReauthenticateHue", "Missing native Hue authorization recovery button");
Check(fields.All(f => f.Name is not ("Actions" or "SendInventoryAtSessionStart" or "AuthPath")), "Legacy configuration remains");
await new HueUserInteractionWrapper(session).RequestUserInteraction(default);
Check(proxy.PairingImage == HuePairingRequest.ImageUrl && proxy.PairingImage.StartsWith("data:image/png;base64,"), "Pairing logo missing");
Check(Convert.FromBase64String(proxy.PairingImage.Split(',')[1]).Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Pairing image not PNG");
await adapter.DisposeAsync();
await HueCredentialChecks.RunAsync();
Console.WriteLine("PASS validation, serialization/cancellation, settings, permissions, availability/status, startup failure, action removal and pairing logo");

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
    public bool IsConnectionUnavailable { get; set; }
    public TaskCompletionSource<bool>? Pending;
    public int Count; public bool? LastState; public bool Success = true; public Guid LastId; public string? LastScene;
    public string? LastUserVisibleError => Success ? null : "Bridge rejected command.";
    public Task<bool> SendHueCommandAsync(Guid id, string type, bool? state = null, string? color = null, double? brightness = null, string? scene = null, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Count++; LastState = state; LastId = id; LastScene = scene; return Task.FromResult(Success); }
    public Task<bool> ControlAllLightsAsync(bool on, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Count++; LastState = on; return Pending?.Task ?? Task.FromResult(Success); }
}
sealed class Data : IHueDataService
{
    public IList<Light> Lights { get; } = [JsonSerializer.Deserialize<Light>("{\"id\":\"11111111-1111-1111-1111-111111111111\",\"metadata\":{\"name\":\"Desk\"}}")!];
    public IList<GroupedLight> Groups { get; } = [];
    public IList<Room> Rooms { get; } = [JsonSerializer.Deserialize<Room>("{\"id\":\"22222222-2222-2222-2222-222222222222\",\"metadata\":{\"name\":\"Office\"},\"services\":[{\"rid\":\"33333333-3333-3333-3333-333333333333\",\"rtype\":\"grouped_light\"}]}")!];
    public IList<Zone> Zones { get; } = [];
    public IList<Scene> Scenes { get; } = [JsonSerializer.Deserialize<Scene>("{\"id\":\"44444444-4444-4444-4444-444444444444\",\"metadata\":{\"name\":\"Relax\"},\"group\":{\"rid\":\"22222222-2222-2222-2222-222222222222\",\"rtype\":\"room\"}}")!];
    public string? LastUserVisibleError => null;
    public bool Success = true;
    public Task<bool> RetrieveBridgeDataAsync() => Task.FromResult(Success);
}

public class SessionProxy : DispatchProxy
{
    public ContextDefinition[] Contexts = [];
    public int ContextWrites;
    public string PairingImage = "";
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "SetContexts") { Contexts = (ContextDefinition[])args![1]!; ContextWrites++; return Task.CompletedTask; }
        if (method.Name == "RequestUserAction")
        {
            var input = args![0]!;
            PairingImage = (string)input.GetType().GetProperty("ImageUrl")!.GetValue(input)!;
            return Task.FromResult<IUserInteractionRequestToken>(null!);
        }
        throw new Exception("Unexpected session feedback: " + method.Name);
    }
}
