using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Execution;

/// <summary>
/// Wraps an <see cref="IToolRegistry"/> and invokes <paramref name="onAfterTool"/>
/// after every <see cref="ExecuteAsync"/> call — used to implement agent step mode.
/// </summary>
internal sealed class StepModeToolRegistry(IToolRegistry inner, Func<CancellationToken, Task> onAfterTool) : IToolRegistry
{
    public IReadOnlyList<ToolDefinition> Definitions => inner.Definitions;
    public DiffInfo? ConsumeDiff() => inner.ConsumeDiff();

    public async Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        var result = await inner.ExecuteAsync(name, args, ct);
        await onAfterTool(ct);
        return result;
    }
}
