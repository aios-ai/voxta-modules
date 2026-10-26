using System.Text.Json;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Model.Shared;

namespace Voxta.Modules.Aios.Tools;

/// <summary>SDK adapter. The module's session owner remains responsible for its clients and monitors.</summary>
internal sealed class ModuleToolsAugmentation(ModuleToolCatalog catalog) : IToolsChatAugmentation
{
    public ServiceTypes[] GetRequiredServiceTypes() => [];

    public ValueTask<IReadOnlyList<ChatTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(catalog.GetTools());
    }

    public ValueTask<ChatToolCallResult> CallToolAsync(
        string name, JsonElement? arguments, CancellationToken cancellationToken) =>
        catalog.CallToolAsync(name, arguments, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
