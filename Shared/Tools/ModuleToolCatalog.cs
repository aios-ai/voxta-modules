using System.Text.Json;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Services.Agents;

namespace Voxta.Modules.Aios.Tools;

/// <summary>One catalog per session. No action dispatch, notes, reply triggers, or retries.</summary>
internal sealed class ModuleToolCatalog
{
    private readonly Dictionary<string, ModuleTool> _tools = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public ModuleToolCatalog(IEnumerable<ModuleTool> tools, ILogger logger)
    {
        _logger = logger;
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool.ExecuteAsync);
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.Definition.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.Definition.Source);
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.Definition.Description);
            if (tool.Definition.InputSchema.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"Tool '{tool.Definition.Name}' needs an object schema.", nameof(tools));

            if (!_tools.TryAdd(tool.Definition.Name, tool with { Definition = Snapshot(tool.Definition) }))
                throw new ArgumentException($"Duplicate tool name '{tool.Definition.Name}'.", nameof(tools));
        }
    }

    // Keep disabled definitions visible to the SDK's discovery/settings UI.
    public IReadOnlyList<ChatTool> GetTools() => _tools.Values.Select(tool => Snapshot(tool.Definition)).ToArray();

    public async ValueTask<ChatToolCallResult> CallToolAsync(
        string name, JsonElement? arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_tools.TryGetValue(name, out var tool) || !tool.Definition.Enabled)
            return new ChatToolCallResult("This tool is unavailable in the current session.", IsError: true);

        if (arguments is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null) })
            return new ChatToolCallResult("Tool arguments must be a JSON object.", IsError: true);

        try
        {
            return await tool.ExecuteAsync(arguments is { ValueKind: JsonValueKind.Null } ? null : arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ModuleToolInputException exception)
        {
            return new ChatToolCallResult(exception.Message, IsError: true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Tool {ToolName} failed", name);
            return new ChatToolCallResult("The tool could not complete the request. Check the module configuration or try again.", IsError: true);
        }
    }

    private static ChatTool Snapshot(ChatTool tool) => tool with
    {
        InputSchema = tool.InputSchema.Clone(),
        ChatStyles = tool.ChatStyles?.ToArray()!,
    };
}
