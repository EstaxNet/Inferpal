using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Execution;

/// <summary>
/// Wraps an <see cref="IToolRegistry"/> and restricts it to read-only analysis tools —
/// plan mode: the agent can explore the codebase and propose a plan, but cannot edit
/// files or execute anything.
/// </summary>
/// <remarks>
/// The whitelist is deliberately NOT <c>AgentLoopPolicy</c>'s read-only set: that set
/// includes <c>run_tests</c> (observing state by executing code is fine for loop
/// detection, not for plan mode). User shell tools and MCP tools have unknown side
/// effects, so anything not explicitly whitelisted is excluded. Filtering
/// <see cref="Definitions"/> keeps write tools out of the model's view; the
/// <see cref="ExecuteAsync"/> guard is the safety net for inline-parsed tool calls
/// (see <c>InlineToolCallParser</c>) that bypass the definition list.
/// </remarks>
internal sealed class PlanModeToolRegistry(IToolRegistry inner) : IToolRegistry
{
    private static readonly HashSet<string> AllowedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "list_files", "search_in_files", "search_codebase", "search_docs",
        "get_diagnostics", "get_active_document", "get_open_editors", "get_solution_info",
        "get_git_status", "get_debugger_state", "generate_project_map", "analyze_code",
        "web_search", "fetch_url",
    };

    /// <summary>Appended to the system prompt while plan mode is active (model-facing, English).</summary>
    public const string SystemPromptSuffix =
        "\n\n## Plan mode (read-only)\n" +
        "You are in plan mode: file modification and command execution tools are disabled. " +
        "Explore the codebase with the read-only tools, then present a clear, step-by-step " +
        "implementation plan and wait for the user to apply it. Do not attempt to write files.";

    internal static bool IsAllowed(string toolName) => AllowedTools.Contains(toolName);

    public IReadOnlyList<ToolDefinition> Definitions =>
        inner.Definitions.Where(d => IsAllowed(d.Function.Name)).ToList();

    public DiffInfo? ConsumeDiff() => inner.ConsumeDiff();

    public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
        IsAllowed(name)
            ? inner.ExecuteAsync(name, args, ct)
            : Task.FromResult(
                $"Tool '{name}' is not available in plan mode (read-only analysis). " +
                "Do not retry it; describe the change as a step of your plan instead.");
}
