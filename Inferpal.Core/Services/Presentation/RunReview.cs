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

/// <summary>Where one change of a file of a run stands.</summary>
internal enum RunPieceState { Pending, Kept, Undone }

/// <summary>One change of a file of a run's review, kept or given back on its own.</summary>
/// <param name="Number">1-based, as the card and <c>/undo-run #run:n</c> name it.</param>
/// <param name="Line">Where it starts in the file now.</param>
/// <param name="Diff">Its lines from before the run and from the run, between up to two unchanged lines on each side.</param>
internal sealed record RunReviewPiece(int Number, int Line, int Added, int Removed, RunPieceState State,
                                     IReadOnlyList<DiffLineModel> Diff);

/// <summary>One file of a run's review card.</summary>
/// <param name="ChangedAfterUndo">Undone, then changed: a redo would lose that change, so none is offered.</param>
/// <param name="Diff">The file before the run against now, context folded; cut past <see cref="RunReview.MaxDiffLines"/>
/// with a closing <c>…</c> line that says how many lines are left out.</param>
/// <param name="Pieces">Its changes, when it holds two or more that can be given back one by one; empty otherwise.</param>
/// <param name="WholeOnly">Why a file whose changes are not offered one by one goes back whole only;
/// <see cref="RunPieces.Whole.None"/> when they are, or when the question does not arise (created, gone, changed since).</param>
internal sealed record RunReviewFile(
    string Path, string Name, int Added, int Removed, bool Created, bool Gone, RunFileState State, bool CanRedo,
    IReadOnlyList<DiffLineModel> Diff, bool ChangedAfterUndo = false, IReadOnlyList<RunReviewPiece>? Pieces = null,
    RunPieces.Whole WholeOnly = RunPieces.Whole.None);

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
            marks.Pieces.TryGetValue(path, out var pieceMarks);
            // ⚠ A kept file is not judged "changed since" above; cut after a hand edit, its "changes" would hold the edit.
            var asTheRunLeft = state is RunFileState.Pending
                            || state is RunFileState.Kept && !history.ChangedSinceRun(run, path, later);
            var (pieces, whole) = asTheRunLeft && !created && exists
                ? PiecesOf(change.SnapshotPath!, path, pieceMarks)
                : ([], RunPieces.Whole.None);
            return new RunReviewFile(path, Path.GetFileName(path), added, removed, created, !exists, state,
                                     // Changes given back one by one are given the run's version again together.
                                     givenBack ? !changedAfterUndo && marks.Redoable.Contains(path)
                                               : asTheRunLeft && pieceMarks is { Undone.Count: > 0 } && File.Exists(pieceMarks.RunCopy),
                                     noCopy ? [] : DiffOf(before, after), changedAfterUndo, pieces, whole);
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

    /// <summary>What the card says of a change's state, the same words as a file's; empty for a change waiting on a
    /// decision.</summary>
    public static string PieceStateLabel(RunPieceState state) => state switch
    {
        // Their own words: in several languages they agree with "change", not with "file".
        RunPieceState.Kept   => Localization.Strings.RunPieceKeptLabel,
        RunPieceState.Undone => Localization.Strings.RunPieceUndoneLabel,
        _                    => string.Empty,
    };

    /// <summary>Why a file goes back whole only, in the words of the card and the text.</summary>
    public static string WholeReason(RunPieces.Whole why) => why switch
    {
        RunPieces.Whole.Binary     => Localization.Strings.RunPiecesWholeBinary,
        RunPieces.Whole.TooLarge   => Localization.Strings.RunPiecesWholeTooLarge(RunPieces.MaxDifferingLines),
        RunPieces.Whole.NotRebuilt => Localization.Strings.RunPiecesWholeNotRebuilt,
        _                          => Localization.Strings.RunPiecesWholeCreated,
    };

    /// <summary>The line under a file whose changes are not offered one by one; empty when they are, or when the question
    /// does not arise.</summary>
    public static string WholeOnlyLabel(RunReviewFile file) => file.WholeOnly == RunPieces.Whole.None
        ? string.Empty
        : Localization.Strings.RunPiecesWholeLabel(WholeReason(file.WholeOnly));

    /// <summary>The note under the card: it speaks of changes too once a file shows them.</summary>
    public static string Note(RunReviewModel review) => review.Files.Any(f => f.Pieces is { Count: > 0 })
        ? Localization.Strings.RunReviewPiecesNote
        : Localization.Strings.RunReviewNote;

    /// <summary>How many changes a file holds, kept and undone, for the line under its name; empty without changes.</summary>
    public static string PiecesSummary(RunReviewFile file) => file.Pieces is { Count: > 0 } pieces
        ? Localization.Strings.RunPiecesSummary(pieces.Count, pieces.Count(p => p.State == RunPieceState.Kept),
                                                pieces.Count(p => p.State == RunPieceState.Undone))
        : string.Empty;

    /// <summary>
    /// The changes of a file, cut from its copy before the run and its version from the run — the copy taken at the first
    /// gesture, else the file now, which the caller found as the run left it. One change is the file itself: none listed.
    /// </summary>
    private static (IReadOnlyList<RunReviewPiece>, RunPieces.Whole) PiecesOf(string snapshot, string path, PieceMarks? marks)
    {
        byte[] before, run;
        try
        {
            before = File.ReadAllBytes(snapshot);
            run    = File.ReadAllBytes(marks?.RunCopy ?? path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("RunReview.PiecesOf", ex);
            return ([], RunPieces.Whole.None);
        }
        if (RunPieces.Of(before, run, out var whole) is not { } cut) return ([], whole);
        if (cut.Pieces.Count < 2) return ([], RunPieces.Whole.None);
        var undone = (marks?.Undone ?? []).ToHashSet();
        var kept   = (marks?.Kept ?? []).ToHashSet();
        var starts = cut.StartLines(undone);
        return ([.. cut.Pieces.Select(p =>
        {
            var (above, below) = cut.Context(p.Index, 2);
            var diff = new List<DiffLineModel>();
            diff.AddRange(above.Select(l => Line(" ", l)));
            diff.AddRange(p.Removed.Select(l => Line("-", l)));
            diff.AddRange(p.Added.Select(l => Line("+", l)));
            diff.AddRange(below.Select(l => Line(" ", l)));
            var state = undone.Contains(p.Index) ? RunPieceState.Undone : kept.Contains(p.Index) ? RunPieceState.Kept : RunPieceState.Pending;
            return new RunReviewPiece(p.Index + 1, starts[p.Index], p.Added.Count, p.Removed.Count, state, diff);
        })], RunPieces.Whole.None);

        static DiffLineModel Line(string prefix, string text) => new() { Prefix = prefix, Text = text.TrimEnd('\r', '\n') };
    }

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
