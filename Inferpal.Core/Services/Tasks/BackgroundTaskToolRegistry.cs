using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Tasks;

/// <summary>
/// The tool surface of a background task: read-only, and additionally free of anything that could
/// raise an approval prompt.
/// </summary>
/// <remarks>
/// <para>Stricter than <see cref="Execution.PlanModeToolRegistry"/> on purpose. Plan mode runs in
/// front of the user, so it can afford <c>web_search</c>/<c>fetch_url</c> and their prompts; a
/// background task runs *while the user is coding*, and a modal approval popping up mid-keystroke
/// is the exact interruption §9 exists to avoid. Anything gated by
/// <c>IApprovalService</c> is therefore out, not just the mutating tools.</para>
/// <para>Two layers, same reason as plan mode: filtering <see cref="Definitions"/> keeps the
/// excluded tools out of the model's view, and the <see cref="ExecuteAsync"/> guard catches
/// inline-parsed calls that never went through the definition list.</para>
/// </remarks>
internal sealed class BackgroundTaskToolRegistry(IToolRegistry inner) : IToolRegistry
{
    private static readonly HashSet<string> AllowedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "list_files", "search_in_files", "search_codebase", "search_docs",
        "get_diagnostics", "get_git_status", "get_solution_info",
        "generate_project_map", "analyze_code",
    };

    /// <summary>Appended to the system prompt of a background run (model-facing, English).</summary>
    internal const string SystemPromptSuffix =
        "\n\n## Background task (read-only)\n" +
        "You are running detached from the conversation, while the user keeps working. You cannot " +
        "write files, run commands or reach the network — only read and analyse. Investigate with " +
        "the read-only tools, then answer with a self-contained report: the user will read it later, " +
        "out of context, so state what you looked at and what you concluded. If the objective " +
        "requires changing code, describe the change precisely instead of attempting it.";

    internal static bool IsAllowed(string toolName) => AllowedTools.Contains(toolName);

    public IReadOnlyList<ToolDefinition> Definitions =>
        inner.Definitions.Where(d => IsAllowed(d.Function.Name)).ToList();

    public DiffInfo? ConsumeDiff() => inner.ConsumeDiff();

    public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
        IsAllowed(name)
            ? inner.ExecuteAsync(name, args, ct)
            : Task.FromResult(
                $"Tool '{name}' is not available to a background task (read-only, no approval prompts). " +
                "Do not retry it; describe what you would do instead, and the user will run it.");
}
