using Inferpal.Services.Persistence;

namespace Inferpal.Services.Execution;

/// <summary>What going back to a question does to one file.</summary>
internal enum RewindFileKind
{
    /// <summary>Put back as it was before the runs that followed the question.</summary>
    PutBack,
    /// <summary>Created by one of those runs: deleted.</summary>
    Deleted,
    /// <summary>The person kept it from a run's review: left as it is.</summary>
    Kept,
}

/// <summary>Why going back to a question touches no file.</summary>
internal enum RewindRefusal
{
    None,
    /// <summary>A file changed after the runs that followed the question: undoing them would overwrite that change.</summary>
    ChangedSince,
    /// <summary>A file changed between two of those runs — by hand, or by a run of another conversation.</summary>
    ChangedBetween,
    /// <summary>A run that followed the question is no longer kept: its changes cannot be undone.</summary>
    NotKept,
    /// <summary>A file's copy from before one of those runs is gone.</summary>
    NoCopy,
    /// <summary>One of those runs is still written by another editor.</summary>
    Busy,
    /// <summary>The runs that followed the question are not the ones the card showed: it is drawn again first.</summary>
    Stale,
}

internal sealed record RewindFile(string Path, RewindFileKind Kind);

/// <summary>Going back to question <paramref name="Turn"/>: the runs that followed it, newest first, and their files.</summary>
/// <param name="Question">The question as it was saved — what went with it named in its last line.</param>
/// <param name="RefusedPath">The file a refusal names; null for <see cref="RewindRefusal.NotKept"/>.</param>
/// <param name="ChangedAt">When the refused file last changed (local time); null when it is gone.</param>
/// <param name="OtherRuns">Runs made after the question outside this conversation — a command such as <c>/tdd</c>, the
/// other editor — that changed only other files: left as they are, and the card says so.</param>
internal sealed record RewindPlan(
    int Turn, string Question, IReadOnlyList<HistoryRun> Runs, IReadOnlyList<RewindFile> Files,
    RewindRefusal Refusal = RewindRefusal.None, string? RefusedPath = null, DateTime? ChangedAt = null, int OtherRuns = 0);

/// <summary>What going back did to the files.</summary>
/// <param name="Failed">Files an undo could not give back: the runs before them were already undone, those after not.</param>
internal sealed record RewindOutcome(RewindPlan Plan, int PutBack, int Deleted, IReadOnlyList<string> Failed);

/// <summary>
/// Going back to a question with its files: the conversation resumes from that question, and the runs that followed it
/// are undone. Decided on the saved conversation (<see cref="SavedMessage.Run"/> names each turn's run), the same in both
/// editors.
/// </summary>
/// <remarks>⚠ Everything is checked before a file is touched, and the undo is never stopped half-way: a file changed after
/// those runs or between two of them — by hand, or by a run that is not theirs — refuses the whole going back. Undone
/// half-way, the conversation would be at a question its files are not at, and the person told nothing they can act on.</remarks>
internal static class Rewind
{
    /// <summary>The turn whose question sits at <paramref name="index"/> of <paramref name="messages"/>; 0 when it is not a
    /// question (a slash command's echo, a note).</summary>
    public static int TurnAt(IReadOnlyList<SavedMessage> messages, int index) =>
        BranchManager.SplitTurns(messages).FirstOrDefault(t => t.EndExclusive - t.MessageCount == index)?.Index ?? 0;

    /// <summary>The question of <paramref name="turn"/> as it was saved; null when there is no such turn.</summary>
    public static string? QuestionAt(IReadOnlyList<SavedMessage> messages, int turn)
    {
        var turns = BranchManager.SplitTurns(messages);
        return turn < 1 || turn > turns.Count ? null : messages[turns[turn - 1].EndExclusive - turns[turn - 1].MessageCount].Content;
    }

    /// <summary>What going back to question <paramref name="turn"/> (1-based, as <c>/branch</c> counts) would do; null when
    /// there is no such question.</summary>
    public static RewindPlan? Plan(FileHistoryService history, IReadOnlyList<SavedMessage> messages, int turn)
    {
        var turns = BranchManager.SplitTurns(messages);
        if (turn < 1 || turn > turns.Count) return null;
        var start    = turns[turn - 1].EndExclusive - turns[turn - 1].MessageCount;
        var question = messages[start].Content;
        var named    = messages.Skip(start).Select(m => m.Run).OfType<string>().Where(id => id.Length > 0)
                               .ToHashSet(StringComparer.Ordinal);
        var all      = history.Runs;
        if (named.Any(id => all.All(r => r.Id != id)))
            return new RewindPlan(turn, question, [], [], RewindRefusal.NotKept);
        var runs = all.Where(r => named.Contains(r.Id) && !r.Undone)
                      .OrderByDescending(r => r.Id, StringComparer.Ordinal).ToList();
        if (runs.Count == 0) return new RewindPlan(turn, question, [], []);
        if (runs.Any(r => r.OpenElsewhere)) return new RewindPlan(turn, question, runs, [], RewindRefusal.Busy);

        var marks = runs.ToDictionary(r => r.Id, history.MarksOf, StringComparer.Ordinal);
        // What the runs that are not these changed, by file: one that came after the first of these to touch a file
        // would be overwritten by undoing it.
        var outside = new Dictionary<string, List<string>>(PathComparer.Default);
        foreach (var other in all.Where(r => !named.Contains(r.Id) && string.CompareOrdinal(r.Id, runs[^1].Id) > 0))
        {
            var otherMarks = history.MarksOf(other);
            foreach (var change in otherMarks.Changes.Where(c => !otherMarks.GivenBack.Contains(c.OriginalPath)))
            {
                if (!outside.TryGetValue(change.OriginalPath, out var ids)) outside[change.OriginalPath] = ids = [];
                ids.Add(other.Id);
            }
        }

        var files = new List<RewindFile>();
        var paths = runs.SelectMany(r => marks[r.Id].Changes.Select(c => c.OriginalPath)
                                                       .Where(p => !marks[r.Id].GivenBack.Contains(p)))
                        .Distinct(PathComparer.Default).ToList();
        foreach (var path in paths)
        {
            // The runs that change it, newest first, each with its change.
            var touching = runs.Select(r => (Run: r, Change: marks[r.Id].Changes.FirstOrDefault(c =>
                                   PathComparer.Default.Equals(c.OriginalPath, path))))
                               .Where(t => t.Change is not null && !marks[t.Run.Id].GivenBack.Contains(path))
                               .Select(t => (t.Run, Change: t.Change!)).ToList();
            // Kept from any of their reviews: every one of them leaves it (ApplyAsync).
            if (touching.Any(t => marks[t.Run.Id].Kept.Contains(path))) { files.Add(new RewindFile(path, RewindFileKind.Kept)); continue; }
            if (touching.Any(t => t.Change.SnapshotFailed || t.Change.SnapshotPath is { } copy && !File.Exists(copy)))
                return new RewindPlan(turn, question, runs, files, RewindRefusal.NoCopy, path);
            if (history.ChangedSinceRun(touching[0].Run, path, new HashSet<string>()))
                return new RewindPlan(turn, question, runs, files, RewindRefusal.ChangedSince, path,
                                      File.Exists(path) ? File.GetLastWriteTime(path) : null);
            var first = touching[^1];
            if (outside.TryGetValue(path, out var others) && others.Any(id => string.CompareOrdinal(id, first.Run.Id) > 0)
                || ChangedBetween(touching))
                return new RewindPlan(turn, question, runs, files, RewindRefusal.ChangedBetween, path);
            files.Add(new RewindFile(path, first.Change.SnapshotPath is null ? RewindFileKind.Deleted : RewindFileKind.PutBack));
        }
        return new RewindPlan(turn, question, runs, files,
                              OtherRuns: outside.Values.SelectMany(ids => ids).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Whether the file changed between two of these runs: what one left is not what the next found.</summary>
    private static bool ChangedBetween(List<(HistoryRun Run, RunChange Change)> touching)
    {
        for (var i = 0; i + 1 < touching.Count; i++)
        {
            var (newer, older) = (touching[i], touching[i + 1]);
            // The state the newer run found: its copy, or no file when it created it. Unknown end (a run cut short): not judged.
            if (older.Run.AfterOf(older.Change.OriginalPath) is not { } left) continue;
            var found = newer.Change.SnapshotPath is { } copy ? FileHistoryService.ContentHash(copy) : string.Empty;
            if (found is not null && found != left) return true;
        }
        return false;
    }

    /// <summary>
    /// Undoes the runs that followed question <paramref name="turn"/>, newest first, after checking them all again — and
    /// that they are still <paramref name="expected"/>, the runs the card showed; the conversation itself is the caller's
    /// (<see cref="BranchManager"/>). A refused plan comes back untouched.
    /// </summary>
    /// <remarks>Never cancelled: a stop between two files would leave the half-undone state the checks exist to prevent.</remarks>
    public static async Task<RewindOutcome?> ApplyAsync(FileHistoryService history, IReadOnlyList<SavedMessage> messages,
                                                        int turn, IReadOnlyCollection<string>? expected)
    {
        if (Plan(history, messages, turn) is not { } plan) return null;
        if (plan.Refusal == RewindRefusal.None && expected is not null
            && !plan.Runs.Select(r => r.Id).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            plan = plan with { Refusal = RewindRefusal.Stale };
        if (plan.Refusal != RewindRefusal.None) return new RewindOutcome(plan, 0, 0, []);
        // Kept from a review: every run of the series that touched it leaves it as it is.
        foreach (var kept in plan.Files.Where(f => f.Kind == RewindFileKind.Kept))
            foreach (var run in plan.Runs)
                if (history.MarksOf(run).Changes.Any(c => PathComparer.Default.Equals(c.OriginalPath, kept.Path)))
                    history.KeepFile(run, kept.Path);
        foreach (var run in plan.Runs)
        {
            var result = await history.UndoRunAsync(run, CancellationToken.None);
            var failed = result.Failed.Concat(result.ChangedSince ?? []).ToList();
            if (failed.Count > 0) return new RewindOutcome(plan, 0, 0, failed);
        }
        // Counted by file: one two runs changed is put back once.
        return new RewindOutcome(plan, plan.Files.Count(f => f.Kind == RewindFileKind.PutBack),
                                 plan.Files.Count(f => f.Kind == RewindFileKind.Deleted), []);
    }
}
