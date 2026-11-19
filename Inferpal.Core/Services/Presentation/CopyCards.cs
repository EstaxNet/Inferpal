using Inferpal.Localization;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Presentation;

/// <summary>One row of the start card: what it says (<paramref name="Label"/>) and its value, identifiers in backticks.</summary>
internal sealed record CopyCardRow(string Label, string Value);

/// <summary>The start card of a run on a copy, the same in every editor: what the copy starts from, and what does not go with
/// it. Texts with identifiers write them in backticks.</summary>
/// <param name="Uncommitted">What does not go with the copy, as a title; <c>null</c> when everything is committed, or the
/// question goes to a copy already made.</param>
/// <param name="UncommittedFiles">The first of them, by path; a last line counts the rest.</param>
internal sealed record CopyStartCardModel(string Title, string Lead, IReadOnlyList<CopyCardRow> Rows, string? Uncommitted,
                                          IReadOnlyList<string> UncommittedFiles, string? UncommittedHint, string Rules, string Go,
                                          string Mine, string Cancel);

/// <summary>The labels of the review card, and its counted texts for every number of kept changes: the person keeps and drops
/// in the editor, which shows the text for the count without a round trip.</summary>
/// <param name="Counts">"3 files · 5 changes · 4 kept", at index = changes kept.</param>
/// <param name="Apply">The apply button, at index = changes kept.</param>
/// <param name="Partial">Why a partial apply is a combination never checked, at index = changes kept; empty when all or none.</param>
/// <param name="Nothing">Said when the run changed nothing; <c>null</c> otherwise (a review that could not be read says why in
/// <paramref name="LeftOut"/>, never "nothing").</param>
/// <param name="LeftOut">What the review leaves out, framed (its detail in English); <c>null</c> when nothing.</param>
internal sealed record CopyReviewTexts(string Title, string Keep, string Drop, string Dropped, string Show, string Hide, string Discard,
                                       string? Nothing, string? LeftOut, IReadOnlyList<string> Counts, IReadOnlyList<string> Apply,
                                       IReadOnlyList<string> Partial);

/// <summary>One file's texts on the review card.</summary>
/// <param name="Tag">"new file" or "deleted"; <c>null</c> for a changed file.</param>
/// <param name="Kept">"2 of 2 kept", at index = its changes kept.</param>
/// <param name="HunkLabels">Each change's place ("Lines 12–15"), in the order of its hunks.</param>
/// <param name="HunkKeys">Each change by what it is (<see cref="CopyCards.HunkKey"/>), in the order of its hunks: what a
/// choice is kept under across a review read again.</param>
internal sealed record CopyReviewFileTexts(string? Tag, IReadOnlyList<string> Kept, IReadOnlyList<string> HunkLabels,
                                           IReadOnlyList<string> HunkKeys);

/// <summary>A file whose kept changes were not applied, on the applied card.</summary>
/// <param name="Lines">Where its kept changes are ("lines 40–52").</param>
/// <param name="CopyPath">The file in the copy, to compare with the person's version.</param>
/// <param name="MinePath">The person's own file.</param>
/// <param name="CanMerge">The person's own version is why it was left, and the copy still has the file: comparing and merging
/// are offered.</param>
/// <param name="MergePrompt">The question "Ask the agent to merge it" puts in the message box, the file named as the agent
/// finds it from the folder the person opened.</param>
internal sealed record CopyNotAppliedFile(string Path, string Lines, string CopyPath, string MinePath, bool CanMerge, string MergePrompt);

/// <summary>The kept changes not applied for one reason.</summary>
/// <param name="Note">Where the copy's change still is, when the person's own version is the reason.</param>
internal sealed record CopyNotAppliedGroup(string Title, string? Note, IReadOnlyList<CopyNotAppliedFile> Files);

/// <summary>The card after an apply: what was written, and what was not and why.</summary>
/// <param name="KeptSaid">What the conversation says once the person keeps the copy for now.</param>
internal sealed record CopyAppliedCardModel(string Title, string? Undo, IReadOnlyList<CopyNotAppliedGroup> NotApplied, string Compare,
                                            string Merge, string Discard, string KeepForNow, string KeptSaid);

/// <summary>Builds the cards of a run on a copy from what the Core read: the start, the review, the apply.</summary>
internal static class CopyCards
{
    /// <summary>How many uncommitted paths the start card names before counting the rest.</summary>
    internal const int UncommittedShown = 8;

    /// <summary>
    /// The start card for <paramref name="start"/>; <paramref name="held"/> is the copy a question would continue on — then the
    /// card names the commit IT started from, and says nothing of what the person has not committed since: that copy is
    /// made.
    /// </summary>
    public static CopyStartCardModel Start(CopyStart start, CopyRun? held)
    {
        var of = held is not null ? Strings.CopyStartOfCommit(held.StartCommit)
               : start.Branch is { } b ? Strings.CopyStartOf(b, start.Commit ?? "")
               : Strings.CopyStartOfDetached(start.Commit ?? "");
        var branch = held is null ? Strings.CopyStartBranchNew
                   : held.LeftBefore ? Strings.CopyStartBranchLeft(held.Copy.Branch)
                   : Strings.CopyStartBranchHeld(held.Copy.Branch);
        var rows = new List<CopyCardRow>
        {
            new(Strings.CopyStartOfLabel, of),
            new(Strings.CopyStartBranchLabel, branch),
            new(Strings.CopyStartKeptLabel, Strings.CopyStartKeptValue),
        };
        string? title = null, hint = null;
        var files = new List<string>();
        if (held is null && start.Uncommitted is null)
            title = Strings.CopyStartUncommittedUnknown;
        else if (held is null && start.Uncommitted!.Count > 0)
        {
            title = Strings.CopyStartUncommitted(start.Uncommitted.Count);
            hint = Strings.CopyStartUncommittedHint;
            files.AddRange(start.Uncommitted.Take(UncommittedShown).Select(p => $"`{p}`"));
            if (start.Uncommitted.Count > UncommittedShown) files.Add(Strings.CopyStartMore(start.Uncommitted.Count - UncommittedShown));
        }
        return new CopyStartCardModel(Strings.CopyStartTitle, Strings.CopyStartLead, rows, title, files, hint, Strings.CopyStartRules,
                                      held is null ? Strings.CopyStartGo : Strings.CopyStartGoHeld, Strings.CopyStartMine,
                                      Strings.BtnCancel);
    }

    /// <summary>The review card's texts for <paramref name="review"/>.</summary>
    public static CopyReviewTexts Review(CopyReview review)
    {
        var total = review.Files.Sum(f => f.Hunks.Count);
        var counts = new string[total + 1];
        var apply = new string[total + 1];
        var partial = new string[total + 1];
        for (var kept = 0; kept <= total; kept++)
        {
            counts[kept] = Strings.CopyReviewCount(review.Files.Count, total, kept);
            apply[kept] = Strings.CopyReviewApply(kept);
            partial[kept] = kept > 0 && kept < total ? Strings.CopyReviewPartial(kept, total) : "";
        }
        return new CopyReviewTexts(Strings.CopyReviewTitle, Strings.CopyReviewKeep, Strings.CopyReviewDrop, Strings.CopyReviewDropped,
                                   Strings.CopyReviewShow, Strings.CopyReviewHide, Strings.CopyReviewDiscard,
                                   review.Files.Count == 0 && review.Problem is null ? Strings.CopyReviewNothing : null,
                                   review.Problem is { } p ? Strings.CopyReviewLeftOut(p) : null,
                                   counts, apply, partial);
    }

    /// <summary>One file's texts on the review card.</summary>
    public static CopyReviewFileTexts File(CopyReviewFile file)
    {
        var kept = Enumerable.Range(0, file.Hunks.Count + 1).Select(k => Strings.CopyReviewFileKept(k, file.Hunks.Count)).ToList();
        var labels = file.Hunks.Select(h => Place(h, Strings.CopyReviewLines, Strings.CopyReviewLine)).ToList();
        return new CopyReviewFileTexts(file.Created ? Strings.CopyReviewNewFile : file.Deleted ? Strings.CopyReviewDeletedFile : null,
                                       kept, labels, [.. file.Hunks.Select(h => HunkKey(file.Path, h))]);
    }

    /// <summary>
    /// A change by what it is: its file, where it starts in the commit the copy started from (which never moves), and its
    /// lines. ⚠ Never its number: a review read again after the run went on numbers its changes anew, and a choice kept by
    /// number would drop a change the person never saw.
    /// </summary>
    public static string HunkKey(string path, DiffHunk hunk) =>
        string.Join("\n", [path, hunk.OldStart.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            .. hunk.OldLines, "\u241E", .. hunk.NewLines]);

    /// <summary>
    /// The card after an apply: <paramref name="kept"/> the hunks the person kept, by path in the repository, of
    /// <paramref name="review"/>; changes counted, never files — the person kept changes. <paramref name="inside"/> is the
    /// folder the person opened as a path in the repository ("" or ending in '/'), <paramref name="workspace"/> that folder:
    /// the person's file is found from there, as the agent finds it.
    /// </summary>
    public static CopyAppliedCardModel Applied(CopyApplied applied, IReadOnlyDictionary<string, IReadOnlyCollection<int>> kept,
                                               CopyReview review, WorkCopy copy, string inside, string workspace)
    {
        var files = review.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        int Count(string path) => kept.TryGetValue(path, out var h) ? h.Count : 0;
        var total = kept.Values.Sum(h => h.Count);
        var written = applied.Written.Sum(Count);
        var groups = applied.Left.GroupBy(l => (l.Reason, l.Reason == CopyLeftReason.NotWritten ? l.Why : ""))
            .Select(g =>
            {
                var theirs = g.Key.Reason is CopyLeftReason.ChangedSince or CopyLeftReason.CreatedSince;
                var left = g.Select(l =>
                {
                    var file = files.GetValueOrDefault(l.Path);
                    var inWorkspace = l.Path.StartsWith(inside, PathComparer.Comparison) ? l.Path[inside.Length..] : l.Path;
                    // A file the run deleted in the copy has no copy to compare with, nor a change to bring over but its deletion.
                    return new CopyNotAppliedFile(
                        l.Path, Lines(file, kept.GetValueOrDefault(l.Path)),
                        Path.GetFullPath(Path.Combine(copy.Folder, l.Path)), Path.GetFullPath(Path.Combine(workspace, inWorkspace)),
                        theirs && file is { Deleted: false }, Strings.CopyMergePrompt(inWorkspace, copy.Branch));
                }).ToList();
                return new CopyNotAppliedGroup(Strings.CopyNotApplied(Math.Max(1, g.Sum(l => Count(l.Path))), g.First().Why),
                                               theirs ? Strings.CopyNotAppliedNote(copy.Branch) : null, left);
            }).ToList();
        return new CopyAppliedCardModel(Strings.CopyAppliedTitle(written, total), written > 0 ? Strings.CopyAppliedUndo : null, groups,
                                        Strings.CopyCompare, Strings.CopyMerge, Strings.CopyReviewDiscard, Strings.CopyKeepForNow,
                                        Strings.CopyKeptForNow(copy.Branch));
    }

    /// <summary>Where the kept changes of a file are, short ("lines 12–15, 40–52"); empty when the review does not show it.</summary>
    private static string Lines(CopyReviewFile? file, IReadOnlyCollection<int>? kept) =>
        file is null || kept is null ? ""
            : string.Join(", ", file.Hunks.Where(h => kept.Contains(h.Index)).Select(h => Place(h, Strings.CopyLinesShort, Strings.CopyLineShort)));

    /// <summary>A change's place, 1-based: its lines in the new text, or the lines it removes when it only removes.</summary>
    private static string Place(DiffHunk hunk, Func<int, int, string> lines, Func<int, string> line)
    {
        var (start, count) = hunk.NewLines.Count > 0 ? (hunk.NewStart, hunk.NewLines.Count) : (hunk.OldStart, hunk.OldLines.Count);
        return count <= 1 ? line(start + 1) : lines(start + 1, start + count);
    }
}
