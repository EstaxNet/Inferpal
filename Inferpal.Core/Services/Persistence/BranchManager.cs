using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Persistence;

/// <summary>One conversational turn: a user message plus everything it produced (tool bubbles,
/// the assistant answer) up to the next user message.</summary>
/// <param name="Index">1-based turn number, as typed in <c>/branch &lt;n&gt;</c>.</param>
/// <param name="Preview">One-line preview of the user message.</param>
/// <param name="MessageCount">Messages the turn spans (the user message included).</param>
/// <param name="EndExclusive">Index just past the turn in the source list.</param>
internal sealed record ConversationTurn(int Index, string Preview, int MessageCount, int EndExclusive);

/// <summary>
/// Everything the caller must persist to fork a conversation — computed without touching the
/// disk so the decision is unit-testable and identical in both front-ends.
/// </summary>
/// <param name="ParentName">Session the branch was forked from.</param>
/// <param name="ParentMessages">Full transcript to write to the parent file. It is written on
/// every fork, not only the first: the conversation may have moved on since it was loaded, and
/// branching must never be the operation that loses the half left behind.</param>
/// <param name="ParentIsNew">True when the conversation had no file yet (the parent name was
/// generated). Informational — the parent is saved either way.</param>
/// <param name="ParentParent">Parent link the parent file already had (it may itself be a branch);
/// re-saving it must not flatten the tree.</param>
/// <param name="ParentForkTurn">Fork turn the parent file already had.</param>
/// <param name="BranchName">Free file name for the new branch.</param>
/// <param name="BranchMessages">Transcript of the branch (turns 1..<paramref name="ForkTurn"/>).</param>
/// <param name="ForkTurn">Turn the fork happened at, recorded in the branch file.</param>
internal sealed record BranchPlan(
    string             ParentName,
    List<SavedMessage> ParentMessages,
    bool               ParentIsNew,
    string?            ParentParent,
    int?               ParentForkTurn,
    string             BranchName,
    List<SavedMessage> BranchMessages,
    int                ForkTurn);

/// <summary>
/// Pure conversation-branching logic (ROADMAP 1.4.0 §7): turn splitting, truncation, branch
/// naming and the markdown views. Sessions stay plain files — a branch is a normal session
/// carrying two extra fields (<c>parent</c>, <c>fork_turn</c>), so nothing else in the store,
/// the history rebuild or the UI has to know about branching.
/// </summary>
/// <remarks>
/// Turn semantics are deliberately simple: <c>/branch n</c> keeps turns 1..n <b>complete</b>
/// (the user message of turn n and its answer), and the conversation continues in the branch.
/// To re-ask turn n differently, branch at n-1.
/// </remarks>
internal static class BranchManager
{
    /// <summary>Longest preview kept for a turn in the <c>/branch</c> listing.</summary>
    private const int PreviewLength = 70;

    /// <summary>Suffix marking a branch file: <c>&lt;parent&gt;__b2</c>, <c>__b3</c>, …</summary>
    private const string BranchSuffix = "__b";

    // ── Turns ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits a transcript into turns. Anything before the first user message (a greeting, a
    /// restored banner) belongs to no turn and is always kept by <see cref="TruncateAtTurn"/>.
    /// </summary>
    public static List<ConversationTurn> SplitTurns(IReadOnlyList<SavedMessage> messages)
    {
        var turns  = new List<ConversationTurn>();
        var starts = new List<int>();

        for (var i = 0; i < messages.Count; i++)
            if (messages[i].Role == "user") starts.Add(i);

        for (var t = 0; t < starts.Count; t++)
        {
            var start = starts[t];
            var end   = t + 1 < starts.Count ? starts[t + 1] : messages.Count;
            turns.Add(new ConversationTurn(t + 1, Preview(messages[start].Content), end - start, end));
        }

        return turns;
    }

    /// <summary>
    /// Transcript of turns 1..<paramref name="turn"/> (inclusive). Returns null when the turn
    /// number doesn't exist — the caller turns that into a usage message.
    /// </summary>
    public static List<SavedMessage>? TruncateAtTurn(IReadOnlyList<SavedMessage> messages, int turn)
    {
        var turns = SplitTurns(messages);
        if (turn < 1 || turn > turns.Count) return null;
        return messages.Take(turns[turn - 1].EndExclusive).ToList();
    }

    // ── Naming ────────────────────────────────────────────────────────────────

    /// <summary>
    /// First free <c>&lt;base&gt;__bN</c> name (N ≥ 2). Branching a branch reuses the same base,
    /// so a family stays adjacent in the name-sorted session list instead of growing
    /// <c>__b2__b2__b2</c> tails; the real tree lives in the <c>parent</c> field.
    /// </summary>
    public static string MakeBranchName(string parentName, IEnumerable<string> existingNames)
    {
        var baseName = StripBranchSuffix(parentName);
        var taken    = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName}{BranchSuffix}{n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Name a session gets when branching forces the unsaved parent to disk.</summary>
    public static string MakeParentName(IReadOnlyList<SavedMessage> messages, DateTime localNow)
    {
        var first = messages.FirstOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
        return SessionManager.SessionFileName(localNow, SessionManager.MakeSnippet(first));
    }

    private static string StripBranchSuffix(string name)
    {
        var idx = name.LastIndexOf(BranchSuffix, StringComparison.Ordinal);
        if (idx <= 0) return name;
        var tail = name[(idx + BranchSuffix.Length)..];
        return tail.Length > 0 && tail.All(char.IsDigit) ? name[..idx] : name;
    }

    // ── Planning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes what to persist for <c>/branch &lt;turn&gt;</c>. Null when the transcript has no
    /// such turn. <paramref name="currentName"/> is the file the conversation was loaded from
    /// (null/empty for a conversation that has never been saved).
    /// </summary>
    public static BranchPlan? Plan(
        IReadOnlyList<SavedMessage>   messages,
        int                           turn,
        string?                       currentName,
        IReadOnlyList<SessionSummary> sessions,
        DateTime                      localNow)
    {
        var branchMessages = TruncateAtTurn(messages, turn);
        if (branchMessages is null) return null;

        var names       = sessions.Select(s => s.Name).ToList();
        var parentIsNew = string.IsNullOrWhiteSpace(currentName) || currentName == "last_session";
        var parentName  = parentIsNew ? MakeParentName(messages, localNow) : currentName!;

        if (parentIsNew) names.Add(parentName);

        // Re-saving the parent must preserve its own place in the tree when it is itself a branch.
        var parent = sessions.FirstOrDefault(s => s.Name.Equals(parentName, StringComparison.OrdinalIgnoreCase));

        return new BranchPlan(
            parentName, messages.ToList(), parentIsNew, parent?.Parent, parent?.ForkTurn,
            MakeBranchName(parentName, names), branchMessages, turn);
    }

    // ── Markdown views ────────────────────────────────────────────────────────

    /// <summary>Branch-point listing shown by a bare <c>/branch</c>: every turn, then the family
    /// tree of the current session, then the usage line.</summary>
    public static string FormatBranchPoints(
        IReadOnlyList<SavedMessage>  messages,
        string?                      currentName,
        IReadOnlyList<SessionSummary> sessions)
    {
        var turns = SplitTurns(messages);
        if (turns.Count == 0) return Strings.BranchNoConversation;

        var sb = new StringBuilder();
        sb.AppendLine(Strings.BranchHeader(turns.Count)).AppendLine();

        foreach (var t in turns)
            sb.AppendLine($"**{t.Index}.** {t.Preview}");

        var tree = FormatFamily(currentName, sessions);
        if (tree is not null) sb.AppendLine().AppendLine(tree);

        sb.AppendLine().AppendLine("---").Append(Strings.BranchUsage);
        return sb.ToString();
    }

    /// <summary>
    /// Family tree of <paramref name="currentName"/>: the root it descends from, then every
    /// descendant, the current session marked. Null when the session has neither parent nor child
    /// (nothing worth rendering yet).
    /// </summary>
    public static string? FormatFamily(string? currentName, IReadOnlyList<SessionSummary> sessions)
    {
        if (string.IsNullOrWhiteSpace(currentName)) return null;

        var byName   = sessions.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var children = sessions
            .Where(s => !string.IsNullOrEmpty(s.Parent))
            .ToLookup(s => s.Parent!, StringComparer.OrdinalIgnoreCase);

        // A session with no parent and no branch has no family worth drawing.
        var hasParent = byName.TryGetValue(currentName!, out var self) && !string.IsNullOrEmpty(self.Parent);
        if (!hasParent && !children[currentName!].Any()) return null;

        // Walk up to the root, guarding against a cycle a hand-edited file could introduce.
        var root = currentName!;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        while (byName.TryGetValue(root, out var s) && !string.IsNullOrEmpty(s.Parent)
               && byName.ContainsKey(s.Parent!) && seen.Add(s.Parent!))
            root = s.Parent!;

        var sb = new StringBuilder();
        sb.AppendLine(Strings.BranchTreeHeader).AppendLine();
        AppendNode(sb, root, null, 0, children, currentName!, seen: []);
        return sb.ToString().TrimEnd();
    }

    private static void AppendNode(
        StringBuilder sb, string name, int? forkTurn, int depth,
        ILookup<string, SessionSummary> children, string currentName, HashSet<string> seen)
    {
        if (!seen.Add(name)) return;   // cycle guard

        var indent = new string(' ', depth * 2);
        var marker = name.Equals(currentName, StringComparison.OrdinalIgnoreCase)
            ? $"  ← {Strings.BranchCurrent}" : string.Empty;
        var fork   = forkTurn is { } t ? $"  *({Strings.BranchForkedAt(t)})*" : string.Empty;

        sb.AppendLine($"{indent}- `{name}`{fork}{marker}");

        foreach (var child in children[name].OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            AppendNode(sb, child.Name, child.ForkTurn, depth + 1, children, currentName, seen);
    }

    private static string Preview(string content)
    {
        var line = content.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return line.Length <= PreviewLength ? line : line[..PreviewLength] + "…";
    }
}
