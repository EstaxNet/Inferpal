using System.IO;
using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// Execution logic for <c>/undo-run [list]</c> — reverts every file touched by the most recent
/// agent run (restoring edits, deleting creations), or lists the workspace's tracked runs — kept across restarts.
/// </summary>
/// <remarks>
/// Extracted from the two front-ends, which carried byte-identical copies differing only in how
/// they emitted the answer and how they relativised paths. Same pattern as
/// <see cref="ReplayCommandHandler"/>, but async: the revert itself is I/O.
/// </remarks>
internal static class UndoRunCommandHandler
{
    /// <param name="history">Session change tracker (<c>ToolRegistry.History</c>).</param>
    /// <param name="parts">Tokenised command; <c>parts[1] == "list"</c> switches to listing.</param>
    /// <param name="root">Project root used to shorten paths; may be null or empty.</param>
    public static async Task<string> HandleAsync(
        FileHistoryService history, string[] parts, string? root, CancellationToken ct)
    {
        var runs = history.Runs;

        if (parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var withChanges = runs.Where(r => r.FileCount > 0).ToList();
            if (withChanges.Count == 0) return Strings.UndoRunNone;

            var list = new StringBuilder();
            list.AppendLine(Strings.UndoRunListHeader(withChanges.Count));
            // Runs outlive a restart: one from another day carries its date.
            foreach (var r in withChanges)
                list.Append("- ").AppendLine(Strings.UndoRunListEntry(
                    r.StartedAt.ToString(r.StartedAt.Date == DateTime.Today ? "HH:mm:ss" : "yyyy-MM-dd HH:mm"), r.FileCount));
            return list.ToString().TrimEnd();
        }

        // ⚠ A result bar's Undo names its own run (`#<id>`): the most recent run that changed files may be another one by
        // now — /fix-build, /tdd, /task apply open runs without a bar of their own — and undoing it in this bar's name
        // reverted the wrong files. Named, only that run is undone, and only while no later run changed files after it:
        // its snapshots predate the later run's writes, which the undo would overwrite.
        var named = parts.Length == 2 && parts[1].StartsWith('#') ? parts[1][1..] : null;
        // Only a bare `/undo-run` (or a bar's) reverts. An argument it does not know — a typo of `list`, or a number
        // read as picking a run from the listing — must not revert the most recent run instead.
        if (parts.Length >= 2 && named is null) return Strings.SlashUsage("/undo-run [list]");

        HistoryRun? run;
        if (named is not null)
        {
            run = runs.FirstOrDefault(r => r.Id == named && r.FileCount > 0);
            if (run is null) return Strings.UndoRunNone;
            if (run.Undone) return Strings.UndoRunAlreadyUndone;
            if (runs.FirstOrDefault(r => r.FileCount > 0 && !r.Undone) != run) return Strings.UndoRunNotTheLatest;
        }
        else
        {
            run = runs.FirstOrDefault(r => r.FileCount > 0);
            // Nothing recorded is not nothing changed: a run whose commands could not be watched says so.
            if (run is null)
                return runs.Count > 0 && runs[0].Uncovered.Count > 0
                    ? $"{Strings.UndoRunNone}\n{NotCovered([runs[0]], root)}".TrimEnd()
                    : Strings.UndoRunNone;
            // Undone already: nothing is touched. Moving on to the run before would answer a click on THIS run's Undo
            // button by undoing another one.
            if (run.Undone) return Strings.UndoRunAlreadyUndone;
        }
        // Still recorded into by the other editor (or another agent thread): undone now, its agent writes on top.
        if (run.OpenElsewhere) return Strings.UndoRunWhileBusy;

        // ⚠ The runs after this one changed no file that could be recorded, or they would be the one undone — but what
        // their commands changed unrecorded (a `sed -i` outside git) lies on top of what this undo writes back: said too.
        var said = runs.TakeWhile(r => r != run).Where(r => r.Uncovered.Count > 0).Append(run).ToList();

        var result = await history.UndoRunAsync(run, ct);

        var sb = new StringBuilder();
        sb.AppendLine(Strings.UndoRunResult(result.Restored.Count, result.Deleted.Count));
        if (result.SavedFirst > 0) sb.AppendLine(Strings.UndoRunSavedFirst);
        foreach (var f in result.Restored) sb.AppendLine($"  ↩ {Relativise(f, root)}");
        foreach (var f in result.Deleted)  sb.AppendLine($"  🗑 {Relativise(f, root)}");
        foreach (var f in result.Failed)   sb.AppendLine($"  ⚠ {Relativise(f, root)}");
        // The run stays to undo while a file failed: said, or the retry that brings it back is never tried.
        if (result.Failed.Count > 0) sb.AppendLine(Strings.UndoRunRetryFailed);
        if (run.CutShort) sb.AppendLine(Strings.UndoRunCutShort);
        sb.Append(NotCovered(said, root));
        return sb.ToString().TrimEnd();
    }

    /// <summary>The lines a run's undo shows that are not shown elsewhere.</summary>
    private const int MaxNotCoveredLines = 12;

    /// <summary>
    /// What the run's commands may have changed without its being recorded, and why — and, when it ran commands, that
    /// files git ignores are never recorded.
    /// </summary>
    /// <remarks>⚠ "Undo run" promises the run's changes back. What a command changed outside what could be watched
    /// (no git repository, a background job, a file too large…) is not given back, and a silent undo reads as a whole
    /// one: said under the result, by cause.</remarks>
    private static string NotCovered(IReadOnlyList<HistoryRun> runs, string? root)
    {
        var sb = new StringBuilder();
        var uncovered = runs.SelectMany(r => r.Uncovered).ToList();
        if (uncovered.Count > 0)
        {
            sb.AppendLine(Strings.UndoRunNotCovered);
            foreach (var (call, note) in uncovered.Take(MaxNotCoveredLines))
                sb.Append("  · ").AppendLine(Line(call, note, root));
            if (uncovered.Count > MaxNotCoveredLines)
                sb.Append("  · ").AppendLine(Strings.UndoRunNotCoveredMore(uncovered.Count - MaxNotCoveredLines));
        }
        if (runs.Any(r => r.WatchedCalls > 0)) sb.AppendLine(Strings.UndoRunIgnoredNote);
        return sb.ToString();
    }

    private static string Line(string call, UncoveredNote note, string? root)
    {
        var file = note.File is null ? string.Empty : Relativise(note.File, root);
        return note.Reason switch
        {
            UncoveredReason.NoRepository   => Strings.NotCoveredNoRepository(call),
            UncoveredReason.TooManyFiles   => Strings.NotCoveredTooMany(call),
            UncoveredReason.TooSlow        => Strings.NotCoveredTooSlow(call),
            UncoveredReason.ReadFailed     => Strings.NotCoveredReadFailed(call),
            UncoveredReason.Background     => Strings.NotCoveredBackground(call),
            UncoveredReason.FileTooLarge   => Strings.NotCoveredFileTooLarge(call, file, (WorkTreeWatch.MaxFileBytes + 1024 * 1024 - 1) / (1024 * 1024)),
            UncoveredReason.LfsFile        => Strings.NotCoveredFileLfs(call, file),
            UncoveredReason.SymbolicLink   => Strings.NotCoveredFileLink(call, file),
            UncoveredReason.OutsideWorkspace => Strings.NotCoveredFileOutside(call, file),
            UncoveredReason.UnknownOrigin  => Strings.NotCoveredUnknownOrigin(call, file),
            UncoveredReason.BeforeUnknown  => Strings.NotCoveredBeforeUnknown(call, file),
            UncoveredReason.SavedByEditor  => Strings.NotCoveredSavedByEditor(call, file),
            _                              => Strings.NotCoveredFileUnreadable(call, file),
        };
    }

    /// <summary>Shortens a path under <paramref name="root"/>; anything else passes through.</summary>
    private static string Relativise(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return path;
        try
        {
            var rel = Path.GetRelativePath(root, path);
            return rel.StartsWith("..", StringComparison.Ordinal) ? path : rel;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"UndoRunCommandHandler.Relativise({path})", ex);
            return path;
        }
    }
}
