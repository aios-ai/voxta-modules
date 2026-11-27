using System.Text.Json;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Repositories;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Model.Shared;
using Voxta.Modules.Tools;

namespace Voxta.Modules.KeyboardShortcuts;

internal sealed class ShortcutToolsSession(
    WindowsKeyboard keyboard, IUnitOfWorkFactory unitOfWorkFactory, Guid userId, Guid moduleId, ILogger logger)
    : IToolsChatAugmentation
{
    private const string ShortcutArgument = "shortcut";

    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _groupedTools = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tool, string Shortcut), Shortcut> _offeredBindings = new();
    private ModuleToolCatalog _catalog = new([], logger);

    public ServiceTypes[] GetRequiredServiceTypes() => [];

    public async ValueTask<IReadOnlyList<ChatTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        var current = await ReadSavedShortcutsAsync(cancellationToken);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tools = current.Where(CanOffer).GroupBy(shortcut => shortcut.ToolName, StringComparer.Ordinal)
                .Select(group => CreateTool(group.Key, group.ToArray()));
            _catalog = new ModuleToolCatalog(tools, logger);
            return _catalog.GetTools();
        }
    }

    public ValueTask<ChatToolCallResult> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken)
    {
        ModuleToolCatalog catalog;
        lock (_gate) catalog = _catalog;
        return catalog.CallToolAsync(name, arguments, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // A tool name can have a chat-wide approval. Keep bindings first seen by this chat, even if removed
    // temporarily, so editing or reusing a name cannot redirect an earlier approval to another shortcut.
    private bool CanOffer(Shortcut shortcut)
    {
        var grouped = shortcut.Group != null;
        if (_groupedTools.TryGetValue(shortcut.ToolName, out var wasGrouped) && wasGrouped != grouped) return false;

        var key = (shortcut.ToolName, shortcut.Name);
        if (_offeredBindings.TryGetValue(key, out var offered)) return offered.HasSameBinding(shortcut);

        _groupedTools[shortcut.ToolName] = grouped;
        _offeredBindings[key] = shortcut;
        return true;
    }

    private async Task<Shortcut[]> ReadSavedShortcutsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var module = await unitOfWorkFactory.ReadAsync(userId, uow => uow.Modules.GetByIdAsync(moduleId, cancellationToken));
        return module is { Enabled: true }
            ? ShortcutConfiguration.Read(module.Configuration.GetValueOrDefault(ShortcutConfiguration.FieldName, ""),
                module.Configuration.GetValueOrDefault(ShortcutSettings.GroupsField, ""), logger)
            : [];
    }

    private ModuleTool CreateTool(string name, Shortcut[] shortcuts)
    {
        var standalone = shortcuts[0].Group == null;
        var description = standalone ? DescribeShortcut(shortcuts[0]) : $"Run a configured shortcut from the {name} group.";
        var schema = standalone ? ChatToolSchema.FromArguments([]) : ShortcutSchema(shortcuts);
        var definition = new ChatTool(name, description, schema, VoxtaModule.ServiceName)
        {
            AugmentationKey = VoxtaModule.AugmentationKey,
            Permission = ToolPermissionClass.Destructive,
        };
        return new ModuleTool(definition, async (arguments, cancellationToken) =>
        {
            var shortcut = ResolveShortcut(shortcuts, arguments);
            var current = await ReadSavedShortcutsAsync(cancellationToken);
            if (!current.Any(shortcut.HasSameBinding))
                throw new ModuleToolInputException("This shortcut was removed or changed. Reopen the chat to use a changed binding.");

            keyboard.Send(shortcut, cancellationToken);
            return new ChatToolCallResult("Shortcut sent.");
        });
    }

    private static string DescribeShortcut(Shortcut shortcut) => shortcut.ForegroundApp is { } app
        ? $"{shortcut.Description.TrimEnd('.')}. Requires {app} to already be in the foreground."
        : shortcut.Description;

    private static JsonElement ShortcutSchema(Shortcut[] shortcuts) => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            [ShortcutArgument] = new
            {
                type = "string",
                description = "Choose one shortcut. " + string.Join("; ", shortcuts.Select(s => $"{s.Name}: {DescribeShortcut(s)}")),
                @enum = shortcuts.Select(s => s.Name).ToArray(),
            },
        },
        required = new[] { ShortcutArgument },
        additionalProperties = false,
    });

    private static Shortcut ResolveShortcut(Shortcut[] shortcuts, JsonElement? arguments)
    {
        if (shortcuts[0].Group == null)
        {
            if (arguments is { } standaloneArgs && standaloneArgs.EnumerateObject().Any())
                throw new ModuleToolInputException("This shortcut takes no arguments.");
            return shortcuts[0];
        }

        if (arguments is not { } groupArgs || groupArgs.EnumerateObject().Count() != 1
            || !groupArgs.TryGetProperty(ShortcutArgument, out var selection) || selection.ValueKind != JsonValueKind.String)
            throw new ModuleToolInputException("Choose one configured shortcut using the shortcut argument.");

        return shortcuts.FirstOrDefault(shortcut => shortcut.Name == selection.GetString())
            ?? throw new ModuleToolInputException("That shortcut is unavailable in this group.");
    }
}
