using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Execution;

/// <summary>
/// Wraps an <see cref="IToolRegistry"/> and restricts it to read-only analysis tools —
/// plan mode: the agent can explore the codebase and propose a plan, but cannot edit
/// files or execute anything.
/// </summary>
/// <remarks>
/// What the mode allows is decided by what a tool CAN do (<see cref="Tools.ToolCeilings"/>): reads and the network (each
/// network call keeps its card), never a write nor a program run — <c>get_diagnostics</c> builds, so it is out, and
/// <c>run_command</c>'s reads (poll, list) do not bring the command in. User shell tools and MCP tools have unknown side
/// effects: out. Filtering <see cref="Definitions"/> keeps them out of the model's view; the <see cref="ExecuteAsync"/>
/// guard is the safety net for inline-parsed tool calls (see <c>InlineToolCallParser</c>) that bypass the definition list.
/// </remarks>
internal sealed class PlanModeToolRegistry(IToolRegistry inner) : IToolRegistry
{
    /// <summary>Appended to the system prompt while plan mode is active (model-facing, English).</summary>
    public const string SystemPromptSuffix =
        "\n\n## Plan mode (read-only)\n" +
        "You are in plan mode: file modification and command execution tools are disabled. " +
        "Explore the codebase with the read-only tools, then present a clear, step-by-step " +
        "implementation plan and wait for the user to apply it. Do not attempt to write files.";

    internal static bool IsAllowed(string toolName) => Tools.ToolCeilings.AllowedInPlan(toolName);

    public IReadOnlyList<ToolDefinition> Definitions =>
        inner.Definitions.Where(d => IsAllowed(d.Function.Name)).ToList();

    public DiffInfo? ConsumeDiff() => inner.ConsumeDiff();

    // A tool the mode refuses gets the mode's refusal, never "turn it back on": turned on, the mode still refuses it.
    public string? TurnedOffRefusal(string name) => IsAllowed(name) ? inner.TurnedOffRefusal(name) : null;

    /// <summary>The inner registry's names this mode allows — a read-only tool turned off included, so that a call written
    /// as text to it is refused with its cause rather than shown as the answer.</summary>
    public IEnumerable<string> CallableNames => inner.CallableNames.Where(IsAllowed);

    public bool Knows(string name) => inner.Knows(name);

    // ⚠ The mode's check comes first: a name it refuses never reaches the registry it wraps, known or not.
    public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
        IsAllowed(name)
            ? inner.Knows(name) ? inner.ExecuteAsync(name, args, ct) : ToolRegistry.AnswerUnknownAsync(inner, name, args, Definitions, ct)
            : !inner.Knows(name) ? Task.FromResult(ToolRegistry.UnknownTool(name, Definitions))
            : Task.FromResult(
                $"Error: tool '{name}' is not available in plan mode (read-only analysis). " +
                "Do not retry it; describe the change as a step of your plan instead.");
}
