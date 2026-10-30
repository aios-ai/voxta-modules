using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.Tools;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Module chat styles preserve saved settings without SDK static fields", () =>
    {
        var field = ModuleToolChatStyles.Create("Test");
        Check(field.Name == "ToolChatStyles", "Saved setting key changed");
        Check(field.Choices.Select(c => c.Value).ToHashSet().SetEquals(Enum.GetValues<ChatStyles>()), "Missing chat styles");
        Check(field.StartValues.SequenceEqual([ChatStyles.Assistant, ChatStyles.Companion]), "Default styles changed");
        var settings = new Voxta.Abstractions.Registration.StaticSettingsSource(new() { [field.Name] = "Roleplay\nAssistant" });
        Check(settings.GetRequired(field).SequenceEqual([ChatStyles.Roleplay, ChatStyles.Assistant]), "Saved styles lost");
        return Task.CompletedTask;
    }),
    ("SDK adapter dispatches arguments, cancellation, and native results", async () =>
    {
        using var cts = new CancellationTokenSource();
        using var args = JsonDocument.Parse("{\"location\":\"Berlin\"}");
        var expected = new ChatToolCallResult("Weather", false) { Pinned = true };
        var tool = Tool("weather", (arguments, token) =>
        {
            Check(arguments!.Value.GetProperty("location").GetString() == "Berlin", "Arguments lost");
            Check(token == cts.Token, "Cancellation token lost");
            return ValueTask.FromResult(expected);
        });
        IToolsChatAugmentation adapter = new ModuleToolsAugmentation(Catalog(tool));
        Check(adapter.GetRequiredServiceTypes().Length == 0, "Tools must not require action inference");
        Check((await adapter.GetToolsAsync(cts.Token)).Single().Name == "weather", "Discovery failed");
        Check(ReferenceEquals(await adapter.CallToolAsync("weather", args.RootElement, cts.Token), expected), "Result metadata lost");
        await adapter.DisposeAsync();
    }),
    ("Unknown, case-mismatched, and disabled tools never execute", async () =>
    {
        var calls = 0;
        var tool = Tool("weather", (_, _) => { calls++; return ValueTask.FromResult(new ChatToolCallResult("ok", false)); });
        var catalog = Catalog(tool with { Definition = tool.Definition with { Enabled = false } });
        foreach (var name in new[] { "weather", "Weather", "unknown" })
            Check((await catalog.CallToolAsync(name, null, default)).IsError, "Unavailable tool succeeded");
        Check(calls == 0, "Unavailable tool executed");
        Check(!catalog.GetTools().Single().Enabled, "Disabled definition missing");
        Check((await Catalog(tool).CallToolAsync("Weather", null, default)).IsError, "Name matching differs from SDK tool names");
    }),
    ("Discovery owns schema lifetime and mutable arrays", () =>
    {
        ModuleToolCatalog catalog;
        var styles = new[] { ChatStyles.Assistant };
        using (var document = JsonDocument.Parse("{\"type\":\"object\"}"))
        {
            var tool = Tool("weather", Success);
            catalog = Catalog(tool with { Definition = tool.Definition with { InputSchema = document.RootElement, ChatStyles = styles } });
        }
        styles[0] = default;
        var discovered = catalog.GetTools().Single();
        Check(discovered.InputSchema.GetProperty("type").GetString() == "object", "Schema lifetime lost");
        Check(discovered.ChatStyles[0] == ChatStyles.Assistant, "Caller changed stored styles");
        discovered.ChatStyles[0] = default;
        Check(catalog.GetTools().Single().ChatStyles[0] == ChatStyles.Assistant, "Discovery changed stored styles");
        return Task.CompletedTask;
    }),
    ("Duplicate names fail before registration", async () =>
    {
        await Throws<ArgumentException>(() => { Catalog(Tool("weather", Success), Tool("weather", Success)); return Task.CompletedTask; });
    }),
    ("Non-object arguments are rejected before executing", async () =>
    {
        var calls = 0;
        var catalog = Catalog(Tool("weather", (_, _) => { calls++; return Success(null, default); }));
        foreach (var json in new[] { "[]", "42", "true", "\"Berlin\"" })
        {
            using var document = JsonDocument.Parse(json);
            Check((await catalog.CallToolAsync("weather", document.RootElement, default)).IsError, "Invalid arguments accepted");
        }
        Check(calls == 0, "Invalid arguments reached handler");
        using var nullDocument = JsonDocument.Parse("null");
        var normalized = Catalog(Tool("empty", (args, _) =>
        {
            Check(args is null, "JSON null not normalized");
            return Success(null, default);
        }));
        Check(!(await normalized.CallToolAsync("empty", nullDocument.RootElement, default)).IsError, "Null arguments rejected");
    }),
    ("Expected input errors remain useful; unexpected exceptions are sanitized", async () =>
    {
        var expected = Catalog(Tool("input", (_, _) => throw new ModuleToolInputException("A location is required.")));
        var result = await expected.CallToolAsync("input", null, default);
        Check(result.IsError && result.Text == "A location is required.", "Input error lost");
        var calls = 0;
        var unexpected = Catalog(Tool("provider", (_, _) => { calls++; throw new InvalidOperationException("secret-token"); }));
        result = await unexpected.CallToolAsync("provider", null, default);
        Check(result.IsError && !result.Text.Contains("secret-token"), "Exception leaked");
        Check(calls == 1, "Side effect was retried");
    }),
    ("Cancellation propagates before dispatch and from handlers", async () =>
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;
        var catalog = Catalog(Tool("weather", (_, _) => { calls++; return Success(null, default); }));
        await Throws<OperationCanceledException>(() => catalog.CallToolAsync("weather", null, cts.Token).AsTask());
        Check(calls == 0, "Canceled request executed");
        var canceled = Catalog(Tool("weather", (_, _) => throw new OperationCanceledException()));
        await Throws<OperationCanceledException>(() => canceled.CallToolAsync("weather", null, default).AsTask());
        IToolsChatAugmentation adapter = new ModuleToolsAugmentation(catalog);
        await Throws<OperationCanceledException>(() => adapter.GetToolsAsync(cts.Token).AsTask());
    }),
};

foreach (var (name, run) in tests)
{
    await run();
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"Passed {tests.Length} tool framework checks.");
await ModuleAuthorizationResetChecks.RunAsync();

static ModuleToolCatalog Catalog(params ModuleTool[] tools) => new(tools, NullLogger.Instance);
static ModuleTool Tool(string name, Func<JsonElement?, CancellationToken, ValueTask<ChatToolCallResult>> handler) =>
    new(new ChatTool(name, "Test tool", JsonSerializer.SerializeToElement(new { type = "object" }), "Aios.Test")
    {
        Enabled = true,
        Permission = ToolPermissionClass.Read,
    }, handler);
static ValueTask<ChatToolCallResult> Success(JsonElement? args, CancellationToken token) =>
    ValueTask.FromResult(new ChatToolCallResult("ok", false));
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static async Task Throws<T>(Func<Task> operation) where T : Exception
{
    try { await operation(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
