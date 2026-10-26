using System.Text.Json;
using Voxta.Abstractions.Services.Agents;

namespace Voxta.Modules.Aios.Tools;

/// <summary>A native SDK definition paired with its session-scoped implementation.</summary>
internal sealed record ModuleTool(
    ChatTool Definition,
    Func<JsonElement?, CancellationToken, ValueTask<ChatToolCallResult>> ExecuteAsync);
