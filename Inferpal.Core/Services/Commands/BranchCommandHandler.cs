using Inferpal.Localization;
using Inferpal.Services.Persistence;

namespace Inferpal.Services.Commands;

/// <summary>
/// What the caller must do after a <c>/branch</c> invocation. Exactly one of the three is set:
/// a bubble to show, a fork to persist, or a session to switch to — the IO stays in the VM
/// (VS) or the host (VS Code), the decision stays here.
/// </summary>
/// <param name="Message">Markdown to display (listing, usage error, unknown branch).</param>
/// <param name="ForkTurn">Turn to fork the conversation at.</param>
/// <param name="SwitchTo">Existing session (branch or parent) to load.</param>
internal sealed record BranchCommandResult(
    string? Message   = null,
    int?    ForkTurn  = null,
    string? SwitchTo  = null);

/// <summary>
/// Execution logic for <c>/branch</c> (ROADMAP 1.4.0 §7) — pure and synchronous, same pattern as
/// <see cref="ReplayCommandHandler"/>:
/// <list type="bullet">
///   <item><c>/branch</c> — list the branch points (turns) and the family tree.</item>
///   <item><c>/branch &lt;n&gt;</c> — fork the conversation at turn <c>n</c>.</item>
///   <item><c>/branch &lt;name&gt;</c> — switch to an existing session/branch.</item>
/// </list>
/// </summary>
internal static class BranchCommandHandler
{
    /// <param name="parts">Tokenised command.</param>
    /// <param name="messages">Current transcript (UI anchors already dropped).</param>
    /// <param name="currentName">Session file the conversation came from, if any.</param>
    /// <param name="sessions">Saved sessions, used for the tree and for name matching.</param>
    public static BranchCommandResult Handle(
        string[]                      parts,
        IReadOnlyList<SavedMessage>   messages,
        string?                       currentName,
        IReadOnlyList<SessionSummary> sessions)
    {
        // ── /branch → branch points + family tree ─────────────────────────────
        if (parts.Length < 2)
            return new BranchCommandResult(
                Message: BranchManager.FormatBranchPoints(messages, currentName, sessions));

        var arg = string.Join(" ", parts[1..]).Trim();

        // ── /branch <n> → fork ────────────────────────────────────────────────
        if (int.TryParse(arg, out var turn))
        {
            var turns = BranchManager.SplitTurns(messages);
            if (turns.Count == 0)             return new BranchCommandResult(Message: Strings.BranchNoConversation);
            if (turn < 1 || turn > turns.Count) return new BranchCommandResult(Message: Strings.BranchInvalidTurn(turns.Count));
            return new BranchCommandResult(ForkTurn: turn);
        }

        // ── /branch <name> → switch ───────────────────────────────────────────
        var match = sessions.FirstOrDefault(s => s.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));
        return match is not null
            ? new BranchCommandResult(SwitchTo: match.Name)
            : new BranchCommandResult(Message: Strings.BranchUnknown(arg));
    }
}
