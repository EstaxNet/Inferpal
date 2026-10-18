using Inferpal.Config;
using Inferpal.Services.Persistence;

namespace Inferpal.Services.Execution;

/// <summary>
/// What the person answered to the calls a narrow rule describes, counted per rule — never the command itself, only
/// the prefix or the folder the rule names — so that <c>/permissions suggest</c> can say "granted 14 times, never refused".
/// </summary>
/// <remarks>
/// Shared by every editor of the machine (<c>%AppData%\Inferpal\approval-history.json</c>), read again before each count.
/// Recomputable state (lost, it only delays a suggestion): not set aside when unreadable, but an unreadable file is said
/// by the suggestion, never read as "nothing to suggest". ⚠ Two editors answering within the same instant can lose one
/// count (the lock is per process): a suggestion is a proposal the person still writes, never an approval.
/// </remarks>
internal sealed class ApprovalHistory
{
    /// <summary>The machine's history; tests point its file elsewhere.</summary>
    internal static ApprovalHistory Shared { get; } = new();

    /// <summary>How many grants, none refused, make a rule worth suggesting.</summary>
    internal const int SuggestAfter = 3;

    /// <summary>One rule's record.</summary>
    internal sealed class Entry
    {
        public string Tool { get; set; } = string.Empty;
        public string Pattern { get; set; } = string.Empty;
        public string Shown { get; set; } = string.Empty;
        public bool IsFolder { get; set; }
        /// <summary>The folder a write rule covers, absolute — what the rule is rebuilt from before it is suggested.</summary>
        public string? Folder { get; set; }
        public int Granted { get; set; }
        public int Refused { get; set; }
        /// <summary>When the rule was first counted: the order of the suggestions, which more answers do not change.</summary>
        public DateTime FirstUtc { get; set; }
        public DateTime LastUtc { get; set; }
    }

    /// <summary>A rule the answers justify.</summary>
    internal sealed record Suggestion(ApprovalRuleProposal.Proposal Rule, int Granted);

    internal AppDataJsonFile<Dictionary<string, Entry>> File { get; } =
        new("approval-history.json", "ApprovalHistory", accept: d => d is not null);

    private readonly object _gate = new();

    /// <summary>Counts one answer to a call <paramref name="rule"/> describes.</summary>
    public void Note(ApprovalRuleProposal.Proposal rule, bool granted)
    {
        lock (_gate)
        {
            var (entries, unreadable) = File.ReadSettled(new Dictionary<string, Entry>(StringComparer.Ordinal));
            // ⚠ A file that cannot be read is not overwritten with one count: every answer it held would be gone.
            if (unreadable) return;
            entries = new Dictionary<string, Entry>(entries, StringComparer.Ordinal);
            var now = DateTime.UtcNow;
            if (!entries.TryGetValue(rule.Key, out var entry))
                entries[rule.Key] = entry = new Entry
                {
                    Tool = rule.Tool, Pattern = rule.Pattern, Shown = rule.Shown, IsFolder = rule.IsFolder,
                    Folder = rule.Folder, FirstUtc = now,
                };
            if (granted) entry.Granted++;
            else entry.Refused++;
            entry.LastUtc = now;
            File.Save(entries);
        }
    }

    /// <summary>
    /// The rules granted at least <see cref="SuggestAfter"/> times and never refused that the machine's rules do not hold
    /// yet, in the order they were first counted — and whether the history could be read at all.
    /// </summary>
    /// <param name="machineRules">The machine's permission rules as the file holds them.</param>
    /// <param name="root">The workspace root: a rule is rebuilt as the card would offer it here, or not suggested.</param>
    /// <remarks>
    /// ⚠ Never the stored regex as such: the file is plain JSON anyone can edit, and <c>/permissions add</c> writes an
    /// <c>allow</c>. An entry whose rule the card would not offer now — edited by hand, or offered by a version whose lists
    /// were wider — is left out, and said in <c>/diagnostics</c>.
    /// </remarks>
    public (IReadOnlyList<Suggestion> List, bool Unreadable) Suggestions(string? machineRules, string? root)
    {
        var (entries, unreadable) = File.ReadSettled(new Dictionary<string, Entry>(StringComparer.Ordinal));
        var held = new HashSet<string>(
            (machineRules ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);
        var list = new List<(Suggestion Item, DateTime First, string Key)>();
        var dropped = 0;
        foreach (var e in entries.Values.Where(e => e.Granted >= SuggestAfter && e.Refused == 0))
        {
            var rebuilt = e.IsFolder
                ? string.IsNullOrEmpty(e.Folder) ? null : ApprovalRuleProposal.For(e.Tool, Path.Combine(e.Folder, "file"), root)
                : ApprovalRuleProposal.For("run_command", e.Shown, root);
            if (rebuilt is null || rebuilt.Pattern != e.Pattern || rebuilt.Shown != e.Shown)
            {
                // Another workspace's folder is not this one's to suggest: not counted as a doubt about the file.
                if (!e.IsFolder || rebuilt is not null) dropped++;
                continue;
            }
            if (rebuilt.Lines.All(held.Contains)) continue;
            list.Add((new Suggestion(rebuilt, e.Granted), e.FirstUtc, rebuilt.Key));
        }
        if (dropped > 0)
            Diagnostics.RecordOnce("ApprovalHistory",
                $"{dropped} rule(s) of {File.Path} are not suggested: an approval card would not offer them now.",
                $"dropped:{dropped}");
        return ([.. list.OrderBy(s => s.First).ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => s.Item)], unreadable);
    }
}

/// <summary>Writes a narrow rule into the machine's permission rules — the one setting an <c>allow</c> may live in.</summary>
internal static class ApprovalRuleWriter
{
    /// <summary>
    /// Adds the lines of <paramref name="rule"/> the rules do not hold yet, and saves that setting alone; <c>null</c> when
    /// saved, else why not.
    /// </summary>
    /// <remarks>⚠ The machine's configuration, never the repository's overlay: an overlay ships with a clone and is
    /// deny-only. ⚠ Added to that one setting as the file holds it (<see cref="InferpalConfig.AddLines"/>): a full save,
    /// during a turn, would also write what this copy holds for its session.</remarks>
    public static string? Add(InferpalConfig config, ApprovalRuleProposal.Proposal rule)
    {
        try
        {
            config.AddLines(nameof(InferpalConfig.PermissionRules), rule.Lines);
            return null;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ApprovalRuleWriter.Add", ex);
            return ex.Message;
        }
    }
}
