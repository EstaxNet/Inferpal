using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Presentation;

/// <summary>Where one file of a run stands in its review.</summary>
internal enum RunFileState
{
    /// <summary>As the run left it, or changed only by the run: Keep and Undo are offered.</summary>
    Pending,
    /// <summary>The person kept it: Undo run leaves it alone.</summary>
    Kept,
    /// <summary>Given back as it was before the run: Redo offered when the version it replaced was saved.</summary>
    Undone,
    /// <summary>Changed after the run ended, by the person or a later run: an undo would lose that change.</summary>
    ChangedSince,
    /// <summary>Its copy from before the run was never taken, or is gone: nothing can give it back.</summary>
    NoCopy,
}

/// <summary>One file of a run's review card.</summary>
/// <param name="ChangedAfterUndo">Undone, then changed: a redo would lose that change, so none is offered.</param>
/// <param name="Diff">The file before the run against now, context folded; cut past <see cref="RunReview.MaxDiffLines"/>
/// with a closing <c>…</c> line that says how many lines are left out.</param>
internal sealed record RunReviewFile(
    string Path, string Name, int Added, int Removed, bool Created, bool Gone, RunFileState State, bool CanRedo,
    IReadOnlyList<DiffLineModel> Diff, bool ChangedAfterUndo = false);

/// <summary>A run's review card: each file it changed, against the state before it.</summary>
/// <param name="Started">When the run began, as the card says it ("before 14:02").</param>
internal sealed record RunReviewModel(string RunId, string Started, IReadOnlyList<RunReviewFile> Files);

/// <summary>Builds <see cref="RunReviewModel"/> — the same card in both editors, and the text <c>/undo-run review</c> shows.</summary>
/// <remarks>Nothing is greyed out while a turn runs: an action is refused then, and says why, and the card is drawn again
/// from the answer — a card disabled for a turn would stay disabled once the turn ended.</remarks>
internal static class RunReview
{
    /// <summary>Diff lines shown per file: a long rewrite says how much more it holds.</summary>
    internal const int MaxDiffLines = 120;

    public static RunReviewModel Build(FileHistoryService history, HistoryRun run)
    {
        var marks = history.MarksOf(run);
        var later = history.TouchedLater(run);
        var files = marks.Changes.OrderBy(c => c.OriginalPath, StringComparer.OrdinalIgnoreCase).Select(change =>
        {
            var path      = change.OriginalPath;
            var exists    = File.Exists(path);
            var created   = change.SnapshotPath is null && !change.SnapshotFailed;
            var noCopy    = change.SnapshotFailed || change.SnapshotPath is { } copy && !File.Exists(copy);
            var before    = !noCopy && change.SnapshotPath is { } snap ? RunSummary.Read(snap) : string.Empty;
            var after     = exists ? RunSummary.Read(path) : string.Empty;
            var (added, removed) = RunSummary.LineDelta(before, after);
            var givenBack = marks.GivenBack.Contains(path);
            var changedAfterUndo = givenBack && !FileHistoryService.AsTheUndoLeft(change);
            // Undone first: a kept file can still be undone by hand, and then offers its Redo.
            var state = givenBack ? RunFileState.Undone
                      : noCopy ? RunFileState.NoCopy
                      : marks.Kept.Contains(path) ? RunFileState.Kept
                      : history.ChangedSinceRun(run, path, later) ? RunFileState.ChangedSince
                      : RunFileState.Pending;
            return new RunReviewFile(path, Path.GetFileName(path), added, removed, created, !exists, state,
                                     givenBack && !changedAfterUndo && marks.Redoable.Contains(path),
                                     noCopy ? [] : DiffOf(before, after), changedAfterUndo);
        }).ToList();
        var started = run.StartedAt.ToString(run.StartedAt.Date == DateTime.Today ? "HH:mm" : "yyyy-MM-dd HH:mm");
        return new RunReviewModel(run.Id, started, files);
    }

    /// <summary>What the card says of a file's state, the same words in both editors and in <c>/undo-run review</c>;
    /// empty for a file waiting on a decision.</summary>
    public static string StateLabel(RunReviewFile file) => file.State switch
    {
        RunFileState.Undone when file.ChangedAfterUndo => Localization.Strings.RunFileChangedSinceUndoLabel,
        RunFileState.Undone       => Localization.Strings.RunFileUndoneLabel,
        RunFileState.Kept         => Localization.Strings.RunFileKeptLabel,
        RunFileState.ChangedSince => Localization.Strings.RunFileChangedLabel,
        RunFileState.NoCopy       => Localization.Strings.RunFileNoCopyLabel,
        _                         => string.Empty,
    };

    /// <summary>A file the run created (or deleted) is all added (or removed) lines: the line diff's own cap would show
    /// "too large" for a long one, and there is nothing to align.</summary>
    private static IReadOnlyList<DiffLineModel> DiffOf(string before, string after)
    {
        before = before.Replace("\r\n", "\n");
        after  = after.Replace("\r\n", "\n");
        if (before.Length == 0 || after.Length == 0)
        {
            var (prefix, text) = before.Length == 0 ? ("+", after) : ("-", before);
            return Cut(text.Length == 0 ? [] : [.. text.TrimEnd('\n').Split('\n').Select(l => new DiffLineModel { Prefix = prefix, Text = l })]);
        }
        return Cut(DiffComputer.Compute(before, after));
    }

    private static IReadOnlyList<DiffLineModel> Cut(List<DiffLineModel> diff)
    {
        if (diff.Count <= MaxDiffLines) return diff;
        var shown = diff.Take(MaxDiffLines).ToList();
        shown.Add(new DiffLineModel { Prefix = "…", Text = Localization.Strings.RunReviewMoreLines(diff.Count - MaxDiffLines) });
        return shown;
    }
}
