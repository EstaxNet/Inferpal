namespace Inferpal.Services.Commands;

/// <summary>
/// <c>/resume-run</c> and the away notice's Resume run: which run to pick up again, and the turn that does — one reader for
/// Visual Studio, the host and ACP.
/// </summary>
internal static class ResumeRunCommandHandler
{
    /// <summary>
    /// The run to resume, among this editor's (<paramref name="editor"/>; <c>null</c>: every run): the one
    /// <paramref name="runId"/> names, else the newest — when the closing cut it, it is not undone, and it carries what to
    /// resume. A run of this editor after it means the person went on: it is not picked up then, unless every later run is
    /// a resumption of it (Regenerate asks it again).
    /// </summary>
    /// <remarks>⚠ By editor, as the away notice says only its own runs: another editor's run is that editor's to resume, and
    /// resumed here it would be offered again there, the work done twice.</remarks>
    internal static HistoryRun? Pick(IEnumerable<HistoryRun> runs, string? runId, string? editor)
    {
        var mine = runs.Where(r => editor is null || r.Editor == editor).ToList();
        var run = runId is { Length: > 0 }
            ? mine.FirstOrDefault(r => r.Id == runId)
            : mine.OrderByDescending(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
        if (run is null || mine.Any(r => string.CompareOrdinal(r.Id, run.Id) > 0 && r.Resume?.ResumedFrom != run.Id)) return null;
        return run is { CutShort: true, Undone: false, Resume: not null, FileCount: > 0 } ? run : null;
    }

    /// <summary>The turn that resumes the run <see cref="Pick"/> finds; <c>null</c> when there is none.</summary>
    /// <remarks>"Changed since" is the judgement <c>/undo-run</c> makes, so the card and the undo never disagree.</remarks>
    internal static RunResumeTurn? Turn(FileHistoryService history, string? runId, string? root, DateTime now)
    {
        if (Pick(history.Runs, runId, history.Editor) is not { } run) return null;
        var later = history.TouchedLater(run);
        return RunResumeTurn.Of(run, path => history.ChangedSinceRun(run, path, later), root, now);
    }

    /// <summary>The run <c>/resume-run #id</c> names (the away notice's button), <c>null</c> for the bare command; and whether
    /// the command carries something that is no run (a mistyped id is named, never taken for the newest run).</summary>
    internal static (string? RunId, bool Malformed) Arg(string command) =>
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList() switch
        {
            [] => (null, false),
            [var one] when one.Length > 1 && one.StartsWith('#') => (one[1..], false),
            _ => (null, true),
        };

    /// <summary>The block the model reads before the bubble's words: the run to resume, under its own heading.</summary>
    internal static string Block(RunResumeTurn turn) => "## Run to resume\n\n" + turn.Prompt;
}
