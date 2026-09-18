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
internal sealed class BackgroundTaskToolRegistry(
    IToolRegistry inner, ProposalRecorder? proposals = null, Func<string?>? currentRoot = null) : IToolRegistry
{
    // The workspace the task started in. Tools read the root LIVE, so under Visual Studio a solution
    // opened while the task runs made its next calls answer about another project: a report that
    // describes two projects as one, and proposals recorded on the other project's files.
    private readonly Func<string?>? _currentRoot = currentRoot;
    private readonly string?        _startRoot   = currentRoot?.Invoke();

    private bool WorkspaceChanged()
    {
        if (_currentRoot is null || string.IsNullOrWhiteSpace(_startRoot)) return false;
        return !string.Equals(Trim(_startRoot), Trim(_currentRoot() ?? string.Empty),
            PathComparer.Comparison);

        static string Trim(string path) =>
            path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    private static readonly HashSet<string> AllowedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "list_files", "search_in_files", "search_codebase", "search_docs",
        "get_diagnostics", "get_git_status", "get_solution_info",
        "generate_project_map", "analyze_code",
    };

    /// <summary>
    /// Additionally exposed when the task runs in <b>proposal mode</b>: the file
    /// mutations whose intent is fully described by a diff.
    /// </summary>
    /// <remarks>
    /// <c>run_command</c> is deliberately absent, and stays absent. Deferring a *command* would mean
    /// storing something to execute later — the blank cheque of §9 wearing a different hat — and a
    /// command has no diff to review, so the user would be approving a sentence rather than a
    /// change. Only what can be shown as old→new can be proposed. <c>rename_symbol</c> and
    /// <c>restore_file</c> are out for the same reason at this stage: their effect spans files the
    /// report would have to summarise rather than show.
    /// </remarks>
    private static readonly HashSet<string> ProposableTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "apply_diff", "apply_edits", "delete_file",
    };

    /// <summary>
    /// Appended to the system prompt of a task running in proposal mode, in place of the read-only
    /// wording. Model-facing English.
    /// </summary>
    internal const string ProposalPromptSuffix =
        "\n\n## Background task (changes are proposed, not applied)\n" +
        "You are running detached from the conversation, while the user keeps working. You may read " +
        "and analyse, and you may use the file-editing tools to express the changes you would make " +
        "— but nothing you write is applied: each edit is recorded as a proposal and the user " +
        "reviews its diff when your report comes back. An editing tool answering that the change " +
        "was not applied is therefore the expected outcome, not a failure: do not retry it, and " +
        "carry on. You cannot run commands or reach the network. Finish with a self-contained " +
        "report: the user reads it later, out of context, so say what you looked at, what you " +
        "concluded, and what each proposed change is for.";

    /// <summary>Appended to the system prompt of a background run (model-facing, English).</summary>
    internal const string SystemPromptSuffix =
        "\n\n## Background task (read-only)\n" +
        "You are running detached from the conversation, while the user keeps working. You cannot " +
        "write files, run commands or reach the network — only read and analyse. Investigate with " +
        "the read-only tools, then answer with a self-contained report: the user will read it later, " +
        "out of context, so state what you looked at and what you concluded. If the objective " +
        "requires changing code, describe the change precisely instead of attempting it.";

    internal static bool IsAllowed(string toolName) => AllowedTools.Contains(toolName);

    /// <summary>True when this tool may be called by a task running in proposal mode.</summary>
    internal static bool IsProposable(string toolName) => ProposableTools.Contains(toolName);

    /// <summary><c>true</c> when changes are recorded as proposals rather than refused outright.</summary>
    private bool ProposalMode => proposals is not null;

    private bool Permits(string toolName) =>
        IsAllowed(toolName) || (ProposalMode && IsProposable(toolName));

    public IReadOnlyList<ToolDefinition> Definitions =>
        inner.Definitions.Where(d => Permits(d.Function.Name)).ToList();

    public DiffInfo? ConsumeDiff() => inner.ConsumeDiff();

    public async Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        if (!Permits(name))
            return $"Tool '{name}' is not available to a background task "
                 + (ProposalMode
                        ? "(it can propose file changes, but cannot run commands or reach the network). "
                        : "(read-only, no approval prompts). ")
                 + "Do not retry it; describe what you would do instead, and the user will run it.";

        if (WorkspaceChanged())
            return "The workspace changed since this background task started (another solution was opened): "
                 + "its tools would now answer about a different project. Do not call more tools; finish your "
                 + "report with what you found so far, and say that the workspace changed.";

        // Proposal mode: the tool asks for approval, the recorder answers no and keeps the diff, so
        // the tool returns its own "cancelled" message. That wording would read to the model as a
        // dead end, and a small local model then either retries or abandons the objective — so the
        // outcome is restated as what it actually is.
        //
        // Compared on RequestCount, not on the proposal count: rewriting the same file replaces its
        // proposal in place, so the collection would not grow and the second write would fall
        // through to the raw cancellation. Matching the tool's message instead would break in ten
        // languages.
        var before = proposals?.RequestCount ?? 0;
        var result = await inner.ExecuteAsync(name, args, ct);
        if (proposals is null || proposals.RequestCount == before) return result;

        // LastOrDefault, not Last: a composite tool can request approval under a subject that
        // records no proposal carrying THIS registration name — Last then threw out of a loop
        // whose contract is never-throw.
        var recorded = proposals.Proposals.LastOrDefault(p =>
            string.Equals(p.Tool, name, StringComparison.OrdinalIgnoreCase));
        if (recorded is null) return result;
        return $"Recorded as a proposal for {recorded.Subject} ({proposals.Count} pending in total). "
             + "Nothing was written: the user reviews this change when your report comes back. "
             + "Continue with the objective.";
    }
}
