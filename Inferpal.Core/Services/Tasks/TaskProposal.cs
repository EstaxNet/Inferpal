using System.Text;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Tasks;

/// <summary>A change a background task wanted to make, recorded instead of applied.</summary>
/// <param name="Tool">Tool that asked — <c>write_file</c>, <c>apply_diff</c>, <c>delete_file</c>…</param>
/// <param name="Subject">Absolute path the change targets; the key a later application uses.</param>
/// <param name="Details">The sentence the approval prompt would have shown.</param>
/// <param name="Diff">Old→new content when the tool supplied one; null for a deletion.</param>
internal sealed record TaskProposal(string Tool, string Subject, string Details, DiffInfo? Diff);

/// <summary>
/// The approval service a background task is given: it <b>records</b> every request and grants
/// none (roadmap §18, the V2 of §9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this and not a new "propose_edit" tool.</b> A background task must be able to say what it
/// would change without changing it. The approval prompt is already the exact point where a change
/// is fully formed — path, content and computed diff all in hand — and where the product decides
/// whether it happens. Recording there means the model uses <c>write_file</c> as it always does, we
/// invent no vocabulary for a 7B to misuse, and there is no second code path that could apply a
/// change while this one is only pretending to.
/// </para>
/// <para>
/// <b>It always returns <c>false</c>, and that is the whole safety argument.</b> Every mutating tool
/// treats a refused approval as "do not touch anything" — no write, no snapshot, no side effect —
/// so a task running against this service cannot modify the workspace even if the model insists.
/// The §9 rule stands untouched: consenting to a task at submission is not consenting to writes
/// nobody can see yet, so nothing is approved here, ever. The diffs are shown <b>when the report
/// comes back</b>, and each one is then applied through the ordinary prompt.
/// </para>
/// <para>
/// ⚠ <b>Not a security boundary — a recorder.</b> It is handed to a registry that only exposes file
/// mutations; a task never sees <c>run_command</c>, <c>fetch_url</c> or <c>web_search</c>. Deferring
/// a *command* would mean storing something to execute later, which is the blank cheque again under
/// another name. Only changes with a reviewable diff can be proposed.
/// </para>
/// </remarks>
internal sealed class ProposalRecorder : IApprovalService
{
    private readonly object _gate = new();
    private readonly List<TaskProposal> _proposals = [];

    /// <summary>Proposals in the order the task made them, latest per file.</summary>
    public IReadOnlyList<TaskProposal> Proposals
    {
        get { lock (_gate) return [.. _proposals]; }
    }

    /// <summary>Distinct proposals held (one per file).</summary>
    public int Count
    {
        get { lock (_gate) return _proposals.Count; }
    }

    private int _requests;

    /// <summary>
    /// Approval requests seen, superseded ones included — <b>monotonic</b>.
    /// </summary>
    /// <remarks>
    /// This is what the registry compares before and after a tool call, and it must not be
    /// <see cref="Count"/>: a task that writes the same file twice replaces its proposal in place, so
    /// the collection does not grow and the second call would look like it never asked. The model
    /// would then receive the tool's raw "cancelled" message and treat the objective as blocked.
    /// </remarks>
    public int RequestCount => Volatile.Read(ref _requests);

    public Task<bool> RequestApprovalAsync(
        string toolName, string details, CancellationToken ct,
        string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
    {
        Interlocked.Increment(ref _requests);
        Record(new TaskProposal(toolName, subject ?? details, details, diff));
        return Task.FromResult(false);   // never granted — see the class remarks
    }

    /// <summary>
    /// Records a proposal, replacing an earlier one for the same file.
    /// </summary>
    /// <remarks>
    /// A task that writes a file twice (a first attempt, then a correction) has one intention, not
    /// two. Keeping both would ask the user to review a change that was already superseded — and
    /// the earlier <c>Diff</c> is stale anyway, since its "new" side is not what the task ended up
    /// wanting. The last word wins, in place, so the report keeps the order the task worked in.
    /// </remarks>
    private void Record(TaskProposal proposal)
    {
        lock (_gate)
        {
            var at = _proposals.FindIndex(p =>
                string.Equals(p.Subject, proposal.Subject, PathComparison));

            if (at >= 0) _proposals[at] = proposal;
            else         _proposals.Add(proposal);
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>Why a proposal cannot be applied, or that it can.</summary>
internal enum ProposalVerdict
{
    /// <summary>Applicable: the file is still in the state the diff was computed from.</summary>
    Ready,
    /// <summary>The file changed since the task looked at it — the diff no longer describes it.</summary>
    Stale,
    /// <summary>Already applied: the file is exactly what the proposal wanted.</summary>
    AlreadyApplied,
    /// <summary>The file is gone (or was never there) and the proposal needed it.</summary>
    Missing,
    /// <summary>Nothing reviewable was recorded — refuse rather than guess.</summary>
    Unusable,
}

/// <summary>
/// Decides whether a recorded proposal can still be applied, and what applying it means.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every proposal applies as a write of the content the user reviewed</b> (or as a deletion),
/// whichever tool originally asked. What was shown was old→new; applying it means producing exactly
/// that "new". Rebuilding tool-specific arguments — a diff hunk, an edit list — would risk applying
/// something subtly different from what was on screen, and reviewing one thing while approving
/// another is the failure this whole feature exists to avoid.
/// </para>
/// <para>
/// <b>A proposal is only valid against the state it was computed from.</b> A background task runs
/// while the user keeps working, so by the time the report is read the file may have moved on.
/// Applying the recorded "new" content then silently destroys whatever was written in between —
/// which is why a changed file is <see cref="ProposalVerdict.Stale"/> and refused rather than
/// merged. The user re-runs the task; the product does not guess.
/// </para>
/// </remarks>
internal static class TaskProposalApplication
{
    /// <param name="Verdict">Whether it can be applied, and why not when it cannot.</param>
    /// <param name="Path">File the change targets.</param>
    /// <param name="Content">Content to write; null for a deletion or a refusal.</param>
    /// <param name="Delete">The proposal is a deletion.</param>
    internal readonly record struct Plan(
        ProposalVerdict Verdict, string Path, string? Content, bool Delete)
    {
        internal bool Ready => Verdict == ProposalVerdict.Ready;
    }

    /// <param name="proposal">The recorded change.</param>
    /// <param name="currentContent">The file's content right now, or null when it does not exist.</param>
    public static Plan Decide(TaskProposal proposal, string? currentContent)
    {
        var deleting = proposal.Tool.Equals("delete_file", StringComparison.OrdinalIgnoreCase);
        var exists   = currentContent is not null;

        if (deleting)
            return new(exists ? ProposalVerdict.Ready : ProposalVerdict.Missing,
                       proposal.Subject, null, Delete: true);

        // No diff means nothing was reviewed, so there is nothing to reproduce faithfully.
        if (proposal.Diff is not { } diff)
            return new(ProposalVerdict.Unusable, proposal.Subject, null, false);

        // Already what the task wanted — checked before staleness, because a file that someone else
        // brought to the same state is not a conflict, it is a no-op.
        if (exists && string.Equals(currentContent, diff.NewText, StringComparison.Ordinal))
            return new(ProposalVerdict.AlreadyApplied, proposal.Subject, null, false);

        // A creation: the proposal assumed nothing was there.
        if (!exists)
            return new(diff.OldText.Length == 0 ? ProposalVerdict.Ready : ProposalVerdict.Missing,
                       proposal.Subject, diff.NewText, false);

        if (!string.Equals(currentContent, diff.OldText, StringComparison.Ordinal))
            return new(ProposalVerdict.Stale, proposal.Subject, null, false);

        return new(ProposalVerdict.Ready, proposal.Subject, diff.NewText, false);
    }

    /// <summary>
    /// Applies one proposal through the <b>real</b> tools, and reports what happened.
    /// </summary>
    /// <param name="tools">The session's own registry — the one whose approval service prompts.</param>
    /// <param name="readFile">Current content of a path, or null when it does not exist.</param>
    /// <param name="beginRun">
    /// Opens a change-tracking run around the write, exactly as a chat turn does. Without it the
    /// snapshot is taken but attaches to no run, so <c>/undo-run</c> answers "nothing to undo" while
    /// the message promises the opposite — verified live on 2026-08-03, and the promise was the part
    /// that was wrong.
    /// </param>
    /// <remarks>
    /// Shared by both front-ends rather than written twice: the interesting part is which tool is
    /// called and against which state, and that must not differ between Visual Studio and VS Code.
    /// Going through the ordinary registry is the point — the user gets the usual approval prompt,
    /// the write is snapshotted, and <c>/undo-run</c> covers it like any other. Nothing here can
    /// bypass that: it has no approval service of its own to consult.
    /// </remarks>
    public static async Task<string> ApplyAsync(
        TaskProposal proposal, IToolRegistry tools, Func<string, string?> readFile,
        CancellationToken ct, Action? beginRun = null)
    {
        var plan = Decide(proposal, readFile(proposal.Subject));

        if (!plan.Ready)
            return plan.Verdict switch
            {
                ProposalVerdict.Stale          => Strings.TaskProposalStale(plan.Path),
                ProposalVerdict.AlreadyApplied => Strings.TaskProposalAlreadyApplied(plan.Path),
                ProposalVerdict.Missing        => Strings.TaskProposalFileMissing(plan.Path),
                _                              => Strings.TaskProposalUnusable(plan.Path),
            };

        var args = plan.Delete
            ? JsonSerializer.SerializeToElement(new { path = plan.Path })
            : JsonSerializer.SerializeToElement(new { path = plan.Path, content = plan.Content });

        // One run per applied proposal: they are approved one at a time, so grouping them together
        // would make a single /undo-run revert changes the user accepted separately.
        beginRun?.Invoke();

        var output = await tools.ExecuteAsync(plan.Delete ? "delete_file" : "write_file", args, ct);

        // Did it actually happen? The user may well have declined the prompt, in which case the tool
        // returns its own refusal and claiming success on top of it would be a lie the user reads as
        // truth. Re-checking the file is the only language-independent answer: matching the tool's
        // message would break in ten locales, and that is precisely the mistake this code base has
        // already made once.
        var after   = readFile(plan.Path);
        var applied = plan.Delete ? after is null
                                  : string.Equals(after, plan.Content, StringComparison.Ordinal);

        return applied ? output + "\n\n" + Strings.TaskProposalApplied(plan.Path) : output;
    }
}

/// <summary>Renders the proposals of a finished background task, diffs included.</summary>
internal static class TaskProposalReport
{
    /// <summary>Lines of diff shown per proposal before it is truncated in the report.</summary>
    internal const int MaxDiffLines = 40;

    /// <summary>
    /// Markdown appended to a task's report: one section per proposed change, numbered so the user
    /// can apply them one at a time.
    /// </summary>
    /// <remarks>
    /// The cap is <see cref="DiffComputer.ComputeText"/>'s own, which already appends its
    /// "+N more diff line(s)" marker. Adding a second truncation here would cut an already-cut diff
    /// and print a line count that is not the real one — so there is exactly one capper, and the
    /// full diff is what the approval prompt shows at application time.
    /// </remarks>
    public static string Render(string taskId, IReadOnlyList<TaskProposal> proposals)
    {
        if (proposals.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("\n\n").Append(Strings.TaskProposalsHeader(proposals.Count)).Append("\n\n");

        for (var i = 0; i < proposals.Count; i++)
        {
            var p = proposals[i];
            sb.Append(Strings.TaskProposalItem(i + 1, p.Tool, p.Details)).Append('\n');

            var diff = p.Diff is null
                ? null
                : DiffComputer.ComputeText(p.Diff.OldText, p.Diff.NewText, MaxDiffLines);
            if (!string.IsNullOrWhiteSpace(diff))
                sb.Append("\n```diff\n").Append(diff).Append("\n```\n");

            sb.Append('\n');
        }

        sb.Append(Strings.TaskProposalsApplyHint(taskId));
        return sb.ToString();
    }
}
