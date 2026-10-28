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
        ["OpenWeatherToolCards"] = "[{\"name\":\"openweather_get_weather\",\"description\":\"Custom weather\",\"disabled\":" + flag + "}]",
        ["ToolChatStyles"] = "Assistant",
    }));
    Check(!saved.Selected.Contains("openweather_get_weather") && saved.Selected.Count == 4, "Disabled boolean/string switch");
    Check(saved.Descriptions!["openweather_get_weather"] == "Custom weather" && saved.ChatStyles.SequenceEqual(new[] { ChatStyles.Assistant }), "Saved descriptions/styles");
    var field = (FormArrayOfObjectsField)OpenWeatherToolsSettings.ConfigurationField(new StaticSettingsSource(new()
    { ["OpenWeatherToolCards"] = "[{\"Name\":\"openweather_get_weather\",\"Disabled\":true}]" }));
    Check(JsonSerializer.Deserialize<OpenWeatherToolSettings[]>(field.DefaultValue!)!.Single(c => c.Name == "openweather_get_weather").Disabled == "true", "Card round trip");
}
var fields = await new ModuleConfigurationProvider().GetModuleConfigurationFieldsAsync(null!, new StaticSettingsSource(new()), default);
Check(fields.OfType<FormPasswordField>().Single().Name == "ApiKey", "API key stays a host-encrypted password");
Check(fields.Any(f => f.Name == "Actions") && fields.Any(f => f.Name == "OpenWeatherToolCards"), "Actions and tools settings coexist");
Console.WriteLine("PASS tool cards, defaults, saved descriptions/styles and encrypted-key configuration");

var settings = new OpenWeatherChatAugmentationsSettings
{
    Actions = ModuleConfigurationProvider.DefaultActions, MyLocation = "Berlin, DE", Units = "imperial",
    WeatherDetails = ["Temp", "Wind", "Precipitation"], PollutionDetails = ["AQI", "PM2.5"], TileCachePath = "test-map-cache",
};
var client = DispatchProxy.Create<IOpenWeatherClient, WeatherClientProxy>();
var fake = (WeatherClientProxy)client;
ModuleToolCatalog Catalog(OpenWeatherChatAugmentationsSettings? config = null, OpenWeatherToolsSettings? tools = null, CancellationToken abort = default) =>
    OpenWeatherToolCatalog.Create(new OpenWeatherToolHandler(client, config ?? settings, CultureInfo.InvariantCulture, abort), tools ?? defaults, NullLogger.Instance);
var catalog = Catalog();
var augmentation = new ModuleToolsAugmentation(catalog);
Check(augmentation.GetRequiredServiceTypes().Length == 0, "Tools must not require action inference");
var definitions = await augmentation.GetToolsAsync(default);
Check(definitions.Count == 5 && definitions.All(t => t.Permission == ToolPermissionClass.Read && t.ExternalContent
    && t.Source == VoxtaModule.ServiceName && t.AugmentationKey == VoxtaModule.ToolsAugmentationKey && t.Enabled), "Tool metadata and read permissions");

foreach (var operation in new[] { "get_weather", "get_weather_forecast", "get_air_pollution", "get_air_pollution_forecast", "get_weather_map" })
{
    fake.Calls.Clear();
    var argumentsJson = operation == "get_weather_map" ? "{\"layer\":\"temp\",\"location\":\"Germany\"}" : "{\"location\":\"Frankfurt am Main, DE\"}";
    var result = await Call(catalog, operation, argumentsJson);
    Check(!result.IsError && fake.Calls.Count == 1, operation + " dispatches once");
    Check(result.Text.Contains(operation == "get_weather_map" ? "DE" : "Frankfurt am Main, DE"), operation + " preserves location");
    if (operation == "get_weather")
    {
        Check(result.Text.Contains("°F") && result.Text.Contains("mph") && result.Text.Contains("Rain: 2"), "Current units and precipitation selection");
        Check(!result.Text.Contains("Cloud cover"), "Unselected details omitted");
    }
    if (operation == "get_weather_forecast") Check(result.Text.Contains("°F") && result.Text.Contains("mph"), "Forecast units");
    if (operation == "get_air_pollution") Check(result.Text.Contains("AQI 2") && result.Text.Contains("PM2.5") && !result.Text.Contains("CO:"), "Pollution detail selection");
    if (operation == "get_weather_map")
    {
        Check(result.Images is { Count: 1 } && fake.Layer == "temp_new" && fake.Cache == settings.TileCachePath, "Native image, normalized layer and cache path");
        Check(fake.Target == (OpenWeatherChatAugmentationsServiceInstance.MapTargetType.Country, "DE"), "Country resolution");
    }
    else Check(fake.Units == "imperial" || operation.StartsWith("get_air"), "Configured units passed to client");

    fake.Fail = true;
    result = await Call(catalog, operation, argumentsJson);
    Check(result.IsError && result.Text == "Provider unavailable", operation + " provider failure");
    fake.Fail = false;
    fake.Cancel = true;
    await ExpectCancellation(() => Call(catalog, operation, argumentsJson));
    fake.Cancel = false;
}
Console.WriteLine("PASS all five operations, details, units, maps, provider failures and in-flight cancellation");

await Call(catalog, "get_weather", "{}");
Check(fake.Location == "Berlin, DE", "Configured location fallback");
var missing = Catalog(new OpenWeatherChatAugmentationsSettings { Actions = [], TileCachePath = "cache" });
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
Check(fake.Target.Type == OpenWeatherChatAugmentationsServiceInstance.MapTargetType.Global, "Maps default global without My Location");
await Call(catalog, "get_weather_map", "{\"layer\":\"wind\",\"location\":\"Europe\"}");
Check(fake.Target.Type == OpenWeatherChatAugmentationsServiceInstance.MapTargetType.Continent && fake.Target.Identifier == "EU", "Continent map");
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
var legacyPlan = plans.Single(p => p.Key == VoxtaModule.AugmentationKey);
Check(plans.Length == 2 && toolsPlan.Activation == AugmentationActivation.OptIn && !toolsPlan.IncludeInCatchAll && toolsPlan.Tools.Count == 5, "Separate opt-in plans and declared tools");
Check(legacyPlan.IncludeInCatchAll && legacyPlan.Tools.Count == 0, "Legacy plan retained");
var session = DispatchProxy.Create<IChatSessionChatAugmentationApi, ToolsSelectedSession>();
Check(await legacyPlan.ActivateAsync(session, default) == null, "Tools suppress legacy activation when both selected");
Check(ModuleConfigurationProvider.DefaultActions.Length == 5, "All legacy actions retained");
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
        ["OpenWeatherToolCards"] = "[{\"Name\":\"openweather_get_weather_map\",\"Disabled\":true}]",
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
    Check(!(await tools.CallToolAsync("openweather_get_weather", JsonSerializer.Deserialize<JsonElement>("{}"), default)).IsError && fake.Units == "metric", "Activated tools dispatch with domain settings");
}
((ToolsSelectedSession)session).ToolsSelected = false;
instanceSettings.ModuleConfiguration["Actions"] = JsonSerializer.Serialize(ModuleConfigurationProvider.DefaultActions);
await using (var legacyInstance = await configuredPlans.Single(p => p.Key == VoxtaModule.AugmentationKey).ActivateAsync(session, default))
{
    Check(legacyInstance is IActionInferenceAugmentation && legacyInstance is not IToolsChatAugmentation, "Legacy-only activation");
    Check(((IActionInferenceAugmentation)legacyInstance!).GetRequiredServiceTypes().SequenceEqual(new[] { ServiceTypes.ActionInference }), "Legacy service requirement retained");
    var context = ((OpenWeatherChatAugmentationsServiceInstance)legacyInstance!).RegisterChatContext().Single();
    Check(context.Actions!.Length == 5, "Legacy actions still register");
}
Console.WriteLine("PASS tools service registration, opt-in plans, legacy retention and duplicate-activation suppression");

static Task<ChatToolCallResult> Call(ModuleToolCatalog catalog, string operation, string json) =>
    catalog.CallToolAsync("openweather_" + operation, JsonSerializer.SerializeToElement(JsonSerializer.Deserialize<JsonElement>(json)), default).AsTask();
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task ExpectCancellation(Func<Task<ChatToolCallResult>> call)
{
    try { await call(); throw new Exception("Cancellation was swallowed"); }
    catch (OperationCanceledException) { }
}

public class ToolsSelectedSession : DispatchProxy
{
    public bool ToolsSelected = true;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "IsAugmentationEnabled" => ToolsSelected && args![0]?.ToString() == VoxtaModule.ToolsAugmentationKey,
        "IsAugmentationSuppressed" => false,
        "get_MainCharacter" => DispatchProxy.Create<ICharacterInferenceData, CharacterProxy>(),
        "get_Abort" => CancellationToken.None,
        "get_SessionId" => Guid.Empty,
        _ => throw new Exception("Unexpected session call: " + method.Name),
    };
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
    public (OpenWeatherChatAugmentationsServiceInstance.MapTargetType Type, string Identifier) Target;
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
            Target = ((OpenWeatherChatAugmentationsServiceInstance.MapTargetType, string))args[0]!;
            Layer = (string)args[1]!; Cache = (string)args[2]!;
            return Task.FromResult(Result(Empty ? Array.Empty<byte>() : new byte[] { 137, 80, 78, 71 }));
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
