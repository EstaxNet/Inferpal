using System.Globalization;
using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

/// <summary>A run the closing cut, as the notice says it: when it started, what it changed, what Undo run can give back.</summary>
/// <param name="RunId">The run (<c>/undo-run #id</c>, the review).</param>
internal sealed record AwayRun(string RunId, string Text, IReadOnlyList<RunFileLine> Files, string Warning);

/// <summary>A background task the notice says: one that finished before the editor closed and was never read, or one the
/// closing stopped (<paramref name="Retry"/>).</summary>
internal sealed record AwayTask(string Id, string Text, bool Retry);

/// <summary>What happened while the editor was closed, said once at the next start (<see cref="AwayNotice"/>).</summary>
internal sealed record AwayNoticeModel(string Title, AwayRun? Run, IReadOnlyList<AwayTask> Tasks)
{
    /// <summary>The runs the notice says — marked said when it is dismissed.</summary>
    public IReadOnlyList<string> RunIds => Run is null ? [] : [Run.RunId];

    /// <summary>The tasks the notice says — marked said when it is dismissed.</summary>
    public IReadOnlyList<string> TaskIds => [.. Tasks.Select(t => t.Id)];
}

/// <summary>
/// The notice of a start: what the closing of the editor cut or left unread — the newest run it cut, and the background
/// tasks it stopped or whose report nobody opened. Said by the editor that ran them, once: dismissed, never again.
/// </summary>
/// <remarks>
/// ⚠ Built from the history of runs and the tasks journal, never from the conversation: the editor saves nothing of a turn
/// it closes on, so the conversation cannot tell. ⚠ The run is this editor's (<see cref="HistoryRun.Editor"/>): another
/// editor's run, cut when it closed, is that editor's to say — named as this one, it would be a false sentence.
/// </remarks>
internal static class AwayNotice
{
    /// <param name="editorSlot">This editor (<c>vs</c>, <c>vscode</c>): only its runs are said.</param>
    /// <param name="editorName">This editor, as the sentences name it ("Visual Studio").</param>
    /// <param name="tasks">The background tasks the queue read from its journal at this start.</param>
    /// <param name="saidRuns">The runs a dismissed notice already said.</param>
    /// <param name="now">Today, for the times: a time of another day carries its date.</param>
    /// <param name="root">The workspace: the files are named under it.</param>
    public static AwayNoticeModel? Build(IEnumerable<HistoryRun> runs, string editorSlot, string editorName,
                                        IReadOnlyList<BackgroundTaskSnapshot> tasks, IReadOnlySet<string> saidRuns,
                                        DateTime now, string? root = null)
    {
        AwayRun? cut = null;
        // The newest run of this editor, by id (local times repeat an hour every autumn): the closing cut it, or nothing.
        // An older cut run is never said — a run after it shows the editor ran on since.
        if (runs.Where(r => r.Editor == editorSlot).OrderByDescending(r => r.Id, StringComparer.Ordinal).FirstOrDefault()
                is { CutShort: true, Undone: false, FileCount: > 0 } run && !saidRuns.Contains(run.Id))
        {
            var files = RunSummary.Files(run).Select(f => f with { Path = Named(f.Path, root) }).ToList();
            var last  = run.LastChange ?? run.StartedAt;
            cut = new AwayRun(run.Id, Strings.AwayRunCut(Time(run.StartedAt, now), editorName, files.Count, Time(last, now)),
                              files, Strings.AwayRunCutWarning(Time(last, now), files.Count));
        }

        var said = new List<AwayTask>();
        // Read (its report opened, or run again) is dealt with, like said.
        // Only what an earlier process left: this session's tasks, and another open window's, did not see the closing.
        foreach (var task in tasks.Where(t => t.Inherited && !t.Said && !t.Read).OrderBy(t => Tasks.TaskJournal.Number(t.Id)))
        {
            if (task.Cause == Tasks.TaskEndCause.EditorClosed)
                said.Add(new(task.Id, task.StartedAt is null
                    ? Strings.AwayTaskStoppedBeforeStart(task.Id, editorName)
                    : Strings.AwayTaskStopped(task.Id, editorName, task.StepsTaken), Retry: true));
            else if (task.State == Tasks.BackgroundTaskState.Succeeded)
                said.Add(new(task.Id, Strings.AwayTaskDone(task.Id, Time((task.FinishedAt ?? task.CreatedAt).LocalDateTime, now), editorName)
                    + " " + (task.PendingProposals.Count > 0 ? Strings.AwayTaskDoneProposalsKept(task.PendingProposals.Count)
                                                             : Strings.AwayTaskDoneKept), Retry: false));
        }
        return cut is null && said.Count == 0 ? null : new(Strings.AwayTitle, cut, said);
    }

    /// <summary>A file as the notice names it: under the workspace, with forward slashes; as it is outside it.</summary>
    private static string Named(string path, string? root)
    {
        var named = Commands.UndoRunCommandHandler.Relativise(path, root);
        return System.IO.Path.IsPathRooted(named) ? named : named.Replace('\\', '/');
    }

    /// <summary>A time as the notice says it: the hour today, the date and the hour another day.</summary>
    internal static string Time(DateTime at, DateTime now) =>
        at.ToString(at.Date == now.Date ? "t" : "g", CultureInfo.CurrentCulture);
}
