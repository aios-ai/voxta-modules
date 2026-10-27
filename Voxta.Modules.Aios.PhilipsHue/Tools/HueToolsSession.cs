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
    private static readonly Guid InventoryId = new("d3c1b11d-e6b1-461a-8e28-51cffda7c2ec");
    private readonly string _basicInventory = HueBasicInventory.Build(hue);
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
            var connected = hue.IsConnected;
            var status = connected ? "" : "Philips Hue is not connected or initialization failed; all Hue tools are unavailable. "
                  + (hue.LastUserVisibleError is { } error ? error + " " : "")
                  + "If Hue control is requested, ask the user to check the bridge is powered on and reachable, then restart the chat. "
                  + "Connection and link-button pairing run at chat startup. No connect tool is available; do not claim lights were changed.";
            var signature = status + (connected ? _basicInventory : "");
            if (signature == _lastStatus) return;
            ContextDefinition[] contexts = connected
                ? [new() { Id = InventoryId, Name = "Philips Hue Basic Inventory", Text = _basicInventory }]
                : [new() { Id = StatusId, Name = "Philips Hue Status", Text = status }];
            await session.SetContexts(VoxtaModule.ServiceName, contexts, cancellationToken);
            _lastStatus = signature;
        }
        finally { _statusGate.Release(); }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
