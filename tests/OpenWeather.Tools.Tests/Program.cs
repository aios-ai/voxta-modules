using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Chats.Objects.Characters;
using Voxta.Abstractions.Encryption;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.VisionCapture;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.OpenWeather;
using Voxta.Modules.Aios.OpenWeather.ChatAugmentations;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.OpenWeather.Configuration;
using Voxta.Modules.Aios.OpenWeather.Tools;
using Voxta.Modules.Aios.Tools;

var defaults = OpenWeatherToolsSettings.From(new StaticSettingsSource(new()));
Check(defaults.Selected.Count == 5 && defaults.ChatStyles.SequenceEqual(new[] { ChatStyles.Assistant, ChatStyles.Companion }), "Tool defaults");
foreach (var flag in new[] { "true", "\"true\"" })
{
    var saved = OpenWeatherToolsSettings.From(new StaticSettingsSource(new()
    {
        ["Actions"] = "invalid legacy JSON must not be read",
        ["OpenWeatherToolCards"] = "[{\"name\":\"get_weather\",\"description\":\"Custom weather\",\"disabled\":" + flag + "}]",
        ["ToolChatStyles"] = "Assistant",
    }));
    Check(!saved.Selected.Contains("get_weather") && saved.Selected.Count == 4, "Disabled boolean/string switch");
    Check(saved.Descriptions!["get_weather"] == "Custom weather" && saved.ChatStyles.SequenceEqual(new[] { ChatStyles.Assistant }), "Saved descriptions/styles");
    var field = (FormArrayOfObjectsField)OpenWeatherToolsSettings.ConfigurationField(new StaticSettingsSource(new()
    { ["OpenWeatherToolCards"] = "[{\"Name\":\"get_weather\",\"Disabled\":true}]" }));
    Check(JsonSerializer.Deserialize<OpenWeatherToolSettings[]>(field.DefaultValue!)!.Single(c => c.Name == "get_weather").Disabled == "true", "Card round trip");
}
var fields = await new ModuleConfigurationProvider().GetModuleConfigurationFieldsAsync(null!, new StaticSettingsSource(new()), default);
var migrated = OpenWeatherToolsSettings.From(new StaticSettingsSource(new()
{
    ["OpenWeatherToolCards"] = "[{\"name\":\"openweather_get_weather\",\"description\":\"Saved weather description\",\"disabled\":true},{\"name\":\"openweather_get_weather_map\",\"disabled\":true},{\"name\":\"get_weather_map\",\"disabled\":false}]",
}));
Check(!migrated.Selected.Contains("get_weather") && migrated.Descriptions!["get_weather"] == "Saved weather description", "Prefixed tool card migration preserves switch/description");
Check(migrated.Selected.Contains("get_weather_map") && migrated.Selected.All(n => !n.StartsWith("openweather_")), "Explicit current name takes precedence over old card");
var migratedField = (FormArrayOfObjectsField)OpenWeatherToolsSettings.ConfigurationField(new StaticSettingsSource(new()
{ ["OpenWeatherToolCards"] = "[{\"Name\":\"openweather_get_weather_map\",\"Disabled\":true}]" }));
Check(JsonSerializer.Deserialize<OpenWeatherToolSettings[]>(migratedField.DefaultValue!)!.Single(c => c.Name == "get_weather_map").Disabled == "true", "Card editor saves migrated short names");
Check(fields.OfType<FormPasswordField>().Single().Name == "ApiKey", "API key stays a host-encrypted password");
Check(fields.All(f => f.Name != "Actions") && fields.Any(f => f.Name == "OpenWeatherToolCards"), "Legacy action configuration removed");
Console.WriteLine("PASS tool cards, defaults, saved descriptions/styles and encrypted-key configuration");

var settings = new OpenWeatherChatAugmentationsSettings
{
    MyLocation = "Berlin, DE", Units = "imperial",
    WeatherDetails = ["Temp", "Wind", "Precipitation"], PollutionDetails = ["AQI", "PM2.5"], TileCachePath = "test-map-cache",
};
var client = DispatchProxy.Create<IOpenWeatherClient, WeatherClientProxy>();
var fake = (WeatherClientProxy)client;
var attachments = new List<(string Description, IImage Image)>();
Exception? attachmentFailure = null;
Task Attach(string description, IImage image, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    if (attachmentFailure != null) throw attachmentFailure;
    attachments.Add((description, image));
    return Task.CompletedTask;
}
ModuleToolCatalog Catalog(OpenWeatherChatAugmentationsSettings? config = null, OpenWeatherToolsSettings? tools = null, CancellationToken abort = default) =>
    OpenWeatherToolCatalog.Create(new OpenWeatherToolHandler(client, config ?? settings, CultureInfo.InvariantCulture, Attach, abort), tools ?? defaults, NullLogger.Instance);
var catalog = Catalog();
var augmentation = new ModuleToolsAugmentation(catalog);
Check(augmentation.GetRequiredServiceTypes().Length == 0, "Tools must not require action inference");
var definitions = await augmentation.GetToolsAsync(default);
Check(definitions.Count == 5 && definitions.All(t => t.Permission == ToolPermissionClass.Read && t.ExternalContent
    && t.Source == VoxtaModule.ServiceName && t.AugmentationKey == VoxtaModule.ToolsAugmentationKey && t.Enabled), "Tool metadata and read permissions");

foreach (var operation in new[] { "get_weather", "get_weather_forecast", "get_air_pollution", "get_air_pollution_forecast", "get_weather_map" })
{
    fake.Calls.Clear();
    attachments.Clear();
    var argumentsJson = operation == "get_weather_map" ? "{\"layer\":\"temp\",\"location\":\"Germany\"}" : "{\"location\":\"Frankfurt am Main, DE\"}";
    var result = await Call(catalog, operation, argumentsJson);
    Check(!result.IsError && fake.Calls.Count == 1, operation + " dispatches once");
    Check(result.Text.Contains(operation == "get_weather_map" ? "DE" : "Frankfurt am Main, DE"), operation + " preserves location");
    if (operation == "get_weather")
    {
        Check(result.Text.Contains("°F") && result.Text.Contains("mph") && result.Text.Contains("Rain: 2"), "Current units and precipitation selection: " + result.Text);
        Check(!result.Text.Contains("Cloud cover"), "Unselected details omitted");
    }
    if (operation == "get_weather_forecast") Check(result.Text.Contains("°F") && result.Text.Contains("mph"), "Forecast units");
    if (operation == "get_air_pollution") Check(result.Text.Contains("AQI 2") && result.Text.Contains("PM2.5") && !result.Text.Contains("CO:"), "Pollution detail selection");
    if (operation == "get_weather_map")
    {
        Check(attachments.Count == 1 && ReferenceEquals(attachments[0].Image, result.Images![0]) && attachments[0].Description.Contains("DE"), "Same map attached once for the user and returned to the model");
        Check(result.Images is { Count: 1 } && fake.Layer == "temp_new" && fake.Cache == settings.TileCachePath, "Native image, normalized layer and cache path");
        Check(fake.Target == (MapTargetType.Country, "DE"), "Country resolution");
    }
    else Check(fake.Units == "imperial" || operation.StartsWith("get_air"), "Configured units passed to client");

    var attachmentCount = attachments.Count;
    fake.Fail = true;
    result = await Call(catalog, operation, argumentsJson);
    Check(result.IsError && result.Text == "Provider unavailable", operation + " provider failure");
    fake.Fail = false;
    fake.Cancel = true;
    await ExpectCancellation(() => Call(catalog, operation, argumentsJson));
    fake.Cancel = false;
    Check(attachments.Count == attachmentCount, "No attachments on provider failure/cancellation");
}
Console.WriteLine("PASS all five operations, details, units, maps, provider failures and in-flight cancellation");

await Call(catalog, "get_weather", "{}");
Check(fake.Location == "Berlin, DE", "Configured location fallback");
var missing = Catalog(new OpenWeatherChatAugmentationsSettings { TileCachePath = "cache" });
fake.Calls.Clear();
Check((await Call(missing, "get_weather", "{}")).IsError && fake.Calls.Count == 0, "Missing location asks user before client call");
foreach (var (operation, argumentsJson) in new[]
{
    ("get_weather", "{\"location\":12}"), ("get_weather_map", "{}"),
    ("get_weather_map", "{\"layer\":true}"), ("get_weather_map", "{\"layer\":\"invalid\"}"),
    ("get_weather_map", "{\"layer\":\"wind\",\"location\":\"Berlin\"}"),
})
{
    fake.Calls.Clear();
    Check((await Call(catalog, operation, argumentsJson)).IsError && fake.Calls.Count == 0, "Invalid arguments rejected: " + argumentsJson);
}
await Call(catalog, "get_weather_map", "{\"layer\":\"clouds\"}");
Check(fake.Target.Type == MapTargetType.Global, "Maps default global without My Location");
await Call(catalog, "get_weather_map", "{\"layer\":\"wind\",\"location\":\"Europe\"}");
Check(fake.Target.Type == MapTargetType.Continent && fake.Target.Identifier == "EU", "Continent map");
foreach (var (layer, unit) in new[] { ("temp", "°C"), ("wind", "m/s"), ("pressure", "hPa"), ("clouds", "%"), ("precipitation", "mm") })
{
    fake.Calls.Clear();
    attachments.Clear();
    var result = await Call(catalog, "get_weather_map", JsonSerializer.Serialize(new { layer, location = "Germany" }));
    Check(!result.IsError && fake.Calls.Count == 1 && attachments.Count == 1, "Metadata adds no provider calls or images");
    Check(result.Text.Contains("Rendered viewport") && result.Text.Contains("north up") && result.Text.Contains("neighboring countries"), "Geographic interpretation context");
    Check(result.Text.Contains("Color legend:") && result.Text.Contains(unit) && result.Text.Contains("https://openweathermap.org/api/map_legend"), "Layer legend and fixed provider units");
    Check(result.Text.Contains("observation time is not supplied") && result.Text.Contains("cannot view the image") && result.Text.Contains("qualitative"), "Honest time and vision limitations");
    if (layer == "precipitation") Check(result.Text.Contains("clear skies") && result.Text.Contains("do not establish zero"), "Precipitation cannot prove clear skies or zero precipitation");
}
var originalCulture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
    var text = WeatherMapPresentation.Describe(new WeatherMapImage([1], new(170, -25.1234, -170, -10, true, 5, 512, 256), true), (MapTargetType.Country, "FJ"), "wind_new");
    Check(text.Contains("south -25.1234") && text.Contains("antimeridian: true") && text.Contains("cache: true"), "Invariant bounds, date-line crossing and cache metadata");
}
finally { CultureInfo.CurrentCulture = originalCulture; }
Console.WriteLine("PASS all five map legends, fixed units, geography, vision limitations and no extra requests");
fake.Empty = true;
foreach (var operation in new[] { "get_weather_forecast", "get_air_pollution", "get_air_pollution_forecast", "get_weather_map" })
    Check((await Call(catalog, operation, operation == "get_weather_map" ? "{\"layer\":\"temp\"}" : "{}")).IsError, "Empty provider data: " + operation);
fake.Empty = false;
fake.Calls.Clear();
var disabled = Catalog(tools: defaults with { Selected = new HashSet<string>() });
Check((await Call(disabled, "get_weather", "{}")).IsError && fake.Calls.Count == 0, "Disabled tool cannot dispatch");
using (var abort = new CancellationTokenSource())
{
    abort.Cancel();
    await ExpectCancellation(() => Call(Catalog(abort: abort.Token), "get_weather", "{}"));
    Check(fake.Calls.Count == 0, "Session cancellation before dispatch");
}
Console.WriteLine("PASS missing/invalid locations, map validation, empty data, disable switches and session cancellation");

var service = new OpenWeatherChatAugmentationsService(null!, null!, NullLoggerFactory.Instance);
Check(service is IToolsChatAugmentationsService, "SDK tools service marker");
var plans = await service.PlanAsync(null!, default);
var toolsPlan = plans.Single(p => p.Key == VoxtaModule.ToolsAugmentationKey);
Check(plans.Length == 1 && toolsPlan.Key == "openweather_tools" && toolsPlan.Label == "OpenWeather"
    && toolsPlan.Activation == AugmentationActivation.OptIn && !toolsPlan.IncludeInCatchAll && toolsPlan.Tools.Count == 5,
    "One tools plan with simplified label, stable key and declared short tool names");
Check(VoxtaModule.Augmentations.Length == 1, "Legacy augmentation removed");
var session = DispatchProxy.Create<IChatSessionChatAugmentationApi, ToolsSelectedSession>();
var factory = new WeatherFactory(client);
var encryption = new TestEncryption();
var configuredService = new OpenWeatherChatAugmentationsService(factory, encryption, NullLoggerFactory.Instance);
var instanceSettings = new NullServiceInstanceSettings
{
    ModuleConfiguration = new()
    {
        ["ApiKey"] = "encrypted:test-key", ["MyLocation"] = "Berlin, DE", ["Units"] = "metric",
        ["Actions"] = "invalid legacy JSON must not be read by tools", ["TileCachePath"] = Path.GetTempPath(),
        ["WeatherDetails"] = "Temp\nWind", ["PollutionDetails"] = "AQI",
        ["OpenWeatherToolCards"] = "[{\"Name\":\"get_weather_map\",\"Disabled\":true}]",
    },
};
await configuredService.InitializeAsync(NullAuthenticationContext.Instance, instanceSettings, default);
var configuredPlans = await configuredService.PlanAsync(null!, default);
await using (var toolsInstance = await configuredPlans.Single(p => p.Key == VoxtaModule.ToolsAugmentationKey).ActivateAsync(session, default))
{
    Check(toolsInstance is IToolsChatAugmentation && toolsInstance is not IActionInferenceAugmentation, "Tools-only activation");
    var tools = (IToolsChatAugmentation)toolsInstance!;
    Check(tools.GetRequiredServiceTypes().Length == 0 && (await tools.GetToolsAsync(default)).Count(t => t.Enabled) == 4, "Runtime settings and no action-inference requirement");
    Check(factory.ApiKey == "test-key" && encryption.Reads == 1, "Decrypt host-managed key once for tools client");
    Check(!(await tools.CallToolAsync("get_weather", JsonSerializer.Deserialize<JsonElement>("{}"), default)).IsError && fake.Units == "metric", "Activated tools dispatch with domain settings");
}
// Use a freshly enabled map to verify the service binds the actual SDK attachment API.
instanceSettings.ModuleConfiguration["OpenWeatherToolCards"] = "[]";
await using (var toolsInstance = await configuredPlans.Single().ActivateAsync(session, default))
{
    var result = await ((IToolsChatAugmentation)toolsInstance!).CallToolAsync("get_weather_map", JsonSerializer.Deserialize<JsonElement>("{\"layer\":\"wind\"}"), default);
    Check(!result.IsError && ((ToolsSelectedSession)session).AttachmentCalls == 1, "Service routes map to SendNoteAttachmentAsync without notes or reply triggers");
}
attachmentFailure = new InvalidOperationException("private attachment failure");
Check((await Call(catalog, "get_weather_map", "{\"layer\":\"temp\"}")).IsError, "Attachment failure must not report map success");
attachmentFailure = new OperationCanceledException();
await ExpectCancellation(() => Call(catalog, "get_weather_map", "{\"layer\":\"temp\"}"));
attachmentFailure = null;
Console.WriteLine("PASS tools-only registration, stable key, short names, native chat attachment dispatch and attachment failure/cancellation");

await WeatherMapChecks.RunAsync();

static Task<ChatToolCallResult> Call(ModuleToolCatalog catalog, string operation, string json) =>
    catalog.CallToolAsync(operation, JsonSerializer.SerializeToElement(JsonSerializer.Deserialize<JsonElement>(json)), default).AsTask();
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task ExpectCancellation(Func<Task<ChatToolCallResult>> call)
{
    try { await call(); throw new Exception("Cancellation was swallowed"); }
    catch (OperationCanceledException) { }
}

public class ToolsSelectedSession : DispatchProxy
{
    public int AttachmentCalls;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "IsAugmentationEnabled" => args![0]?.ToString() == VoxtaModule.ToolsAugmentationKey,
        "IsAugmentationSuppressed" => false,
        "get_MainCharacter" => DispatchProxy.Create<ICharacterInferenceData, CharacterProxy>(),
        "get_Abort" => CancellationToken.None,
        "get_SessionId" => Guid.Empty,
        "SendNoteAttachmentAsync" => Attach(args!),
        _ => throw new Exception("Unexpected session call: " + method.Name),
    };
    private Task Attach(object?[] args)
    {
        ((CancellationToken)args[2]!).ThrowIfCancellationRequested();
        if (args[1] is not IImage image || image.Source != ComputerVisionSource.Tool) throw new Exception("Invalid attachment");
        AttachmentCalls++;
        return Task.CompletedTask;
    }
}

public class CharacterProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "get_Culture"
        ? CultureInfo.InvariantCulture : throw new Exception("Unexpected character call: " + method.Name);
}

public sealed class WeatherFactory(IOpenWeatherClient client) : IOpenWeatherClientFactory
{
    public string? ApiKey;
    public IOpenWeatherClient CreateClient(string apiKey) { ApiKey = apiKey; return client; }
}

public sealed class TestEncryption : ILocalEncryptionProvider
{
    public int Reads;
    public string Decrypt(string value)
    {
        Reads++;
        if (value != "encrypted:test-key") throw new Exception("Unexpected credential");
        return "test-key";
    }
    public string Encrypt(string value) => throw new Exception("Tools must not write credentials");
}

public class WeatherClientProxy : DispatchProxy
{
    public List<string> Calls = [];
    public bool Fail, Cancel, Empty;
    public string? Location, Units, Layer, Cache;
    public (MapTargetType Type, string Identifier) Target;
    private const string Weather = "{\"main\":{\"temp\":62,\"feels_like\":60,\"temp_min\":58,\"temp_max\":65},\"sys\":{\"country\":\"DE\"},\"weather\":[{\"description\":\"clear\",\"icon\":\"01d\"}],\"clouds\":{\"all\":0},\"wind\":{\"speed\":5,\"deg\":90},\"rain\":{\"1h\":2}}";
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    private OpenWeatherResult<T> Result<T>(T value) => Fail
        ? OpenWeatherResult<T>.Fail(OpenWeatherOperationState.Unavailable, "Provider unavailable") : OpenWeatherResult<T>.Ok(value);
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var name = method!.Name;
        Calls.Add(name);
        var token = (CancellationToken)args![^1]!;
        token.ThrowIfCancellationRequested();
        if (Cancel) throw new OperationCanceledException();
        if (name == "FetchWeatherMapAsync")
        {
            Target = ((MapTargetType, string))args[0]!;
            Layer = (string)args[1]!; Cache = (string)args[2]!;
            return Task.FromResult(Result(new WeatherMapImage(Empty ? Array.Empty<byte>() : new byte[] { 137, 80, 78, 71 },
                new(-180, -85.0511, 180, 85.0511, false, 2, 1024, 1024), CompositeFromCache: false)));
        }
        Location = (string)args[0]!;
        if (name == "FetchWeatherData" || name == "FetchForecastData") Units = (string?)args[1];
        return name switch
        {
            "FetchWeatherData" => Task.FromResult(Result(Parse<OpenWeatherResponse>(Weather))),
            "FetchForecastData" => Task.FromResult(Result(Parse<OpenWeatherForecastResponse>("{\"city\":{\"country\":\"DE\",\"coord\":{}},\"list\":" + (Empty ? "[]" : "[" + Weather[..^1] + ",\"dt_txt\":\"2026-10-28 12:00:00\",\"sys\":{}}]") + "}"))),
            "FetchAirPollutionData" or "FetchAirPollutionForecastData" => Task.FromResult(Result(Parse<OpenWeatherAirPollutionResponse>("{\"coord\":{},\"list\":" + (Empty ? "[]" : "[{\"dt\":1793188800,\"main\":{\"aqi\":2},\"components\":{\"pm2_5\":10}}]") + "}"))),
            _ => throw new Exception("Unexpected weather call: " + name),
        };
    }
}
