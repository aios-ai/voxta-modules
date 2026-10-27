using System.Text.Json;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.PhilipsHue.Clients;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.PhilipsHue.Tools;

internal sealed class HueToolsSession(ModuleToolCatalog catalog, HueManager hue, IChatSessionChatAugmentationApi session)
    : IToolsChatAugmentation
{
    private static readonly Guid StatusId = new("686fa260-80be-4f64-8581-d54812321bcd");
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private string? _lastStatus;
    public ServiceTypes[] GetRequiredServiceTypes() => [];

    public async ValueTask<IReadOnlyList<ChatTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        await PublishStatusAsync(cancellationToken);
        return catalog.GetTools().Select(tool => tool with { Enabled = tool.Enabled && hue.IsConnected }).ToArray();
    }

    public async ValueTask<ChatToolCallResult> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken)
    {
        // Recheck availability even for a call offered before the connection changed.
        if (!hue.IsConnected)
        {
            await PublishStatusAsync(cancellationToken);
            return new ChatToolCallResult("Hue is unavailable. Check the bridge and restart the chat to reconnect.", IsError: true);
        }
        var result = await catalog.CallToolAsync(name, arguments, cancellationToken);
        await PublishStatusAsync(cancellationToken);
        return result;
    }

    internal async Task PublishStatusAsync(CancellationToken cancellationToken)
    {
        await _statusGate.WaitAsync(cancellationToken);
        try
        {
            var status = hue.IsConnected
                ? "Philips Hue is connected and its inventory is loaded. Only tools enabled in the module options are offered."
                : "Philips Hue is not connected or initialization failed; all Hue tools are unavailable. "
                  + (hue.LastUserVisibleError is { } error ? error + " " : "")
                  + "If Hue control is requested, ask the user to check the bridge is powered on and reachable, then restart the chat. "
                  + "Connection and link-button pairing run at chat startup. No connect tool is available; do not claim lights were changed.";
            if (status == _lastStatus) return;
            await session.SetContexts(VoxtaModule.ServiceName,
                [new ContextDefinition { Id = StatusId, Name = "Philips Hue Status", Text = status }], cancellationToken);
            _lastStatus = status;
        }
        finally { _statusGate.Release(); }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
