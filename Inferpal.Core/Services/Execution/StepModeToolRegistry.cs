using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Execution;

/// <summary>
/// Wraps an <see cref="IToolRegistry"/> and invokes <paramref name="onAfterTool"/>
/// after every <see cref="ExecuteAsync"/> call — used to implement agent step mode.
/// </summary>
/// <remarks>
/// ⚠ <b>One tool at a time, and this is not an optimisation.</b> The orchestrator runs in parallel
/// (<c>Task.WhenAll</c>) any batch of at least two safe read-only tools — the most common batch
/// shape a model emits. Without the serialisation below, every call in the batch entered the pause
/// <b>at the same time</b>, and both front-ends hold that wait in a <b>single field</b>
/// (<c>HostServer.StepResume</c>, <c>InferpalToolWindowData._stepResume</c>): each pause overwrote
/// the previous one, a single call resumed on "Resume", the others waited for an answer nobody
/// could give any more, and the turn stayed stuck until cancellation. "Step mode" means one tool at
/// a time; the guard therefore lives here, not copied into each adapter.
/// </remarks>
internal sealed class StepModeToolRegistry(IToolRegistry inner, Func<CancellationToken, Task> onAfterTool) : IToolRegistry
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public IReadOnlyList<ToolDefinition> Definitions => inner.Definitions;
    public DiffInfo? ConsumeDiff() => inner.ConsumeDiff();

    public async Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await inner.ExecuteAsync(name, args, ct);
            await onAfterTool(ct);
            return result;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
