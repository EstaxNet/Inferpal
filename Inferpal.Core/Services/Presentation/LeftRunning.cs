using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

/// <summary>A background job a turn started and left running: its id and its command, on one line.</summary>
internal sealed record LeftRunningJob(string Id, string Command);

/// <summary>The card under an answer that lists what its turn left running, with its texts in the interface's language.</summary>
/// <param name="Title">"Still running after this run".</param>
/// <param name="Notes">What happens to them — they run until stopped or until the editor closes — for each number still
/// running: <c>Notes[n - 1]</c> for <c>n</c> jobs, so the note agrees with what a Stop leaves.</param>
/// <param name="Stop">The button of one job.</param>
/// <param name="StopAll">The button of every job, shown from two.</param>
/// <param name="NoneNow">The note once nothing of the card runs any more.</param>
internal sealed record LeftRunningCard(string Title, List<string> Notes, string Stop, string StopAll, string NoneNow,
                                       List<LeftRunningJob> Jobs);

/// <summary>
/// What a run leaves running (pari 168): the background jobs (<c>run_command background:true</c>) a turn started and that
/// still run when it ends. Said under the answer — a card with Stop in the editors, a sentence after the answer in ACP —
/// because nothing else shows them before the editor closes and they die with it.
/// </summary>
/// <remarks>Only the turn's own jobs (<see cref="Shell.BackgroundShellRegistry.Mark"/>): one an earlier turn started is not
/// said again. Screen only: the jobs die with the host, a reloaded conversation has nothing to stop.</remarks>
internal static class LeftRunning
{
    /// <summary>The longest command a row shows; the rest is cut with an ellipsis (the job id names it anyway).</summary>
    private const int MaxCommandChars = 200;

    /// <summary>The jobs of the turn marked by <paramref name="mark"/> that still run; empty without a registry.</summary>
    public static List<LeftRunningJob> Since(Shell.BackgroundShellRegistry? jobs, int mark) =>
        jobs is null ? [] : [.. jobs.RunningSince(mark).Select(j => new LeftRunningJob(j.Id, OneLine(j.Command)))];

    /// <summary>The card for <paramref name="jobs"/> in <paramref name="editor"/> (named in its note); <c>null</c> when the
    /// turn left nothing running.</summary>
    public static LeftRunningCard? Card(List<LeftRunningJob> jobs, string editor) =>
        jobs.Count == 0 ? null
            : new(Strings.LeftRunningTitle, [.. Enumerable.Range(1, jobs.Count).Select(n => Strings.LeftRunningNote(n, editor))],
                  Strings.LeftRunningStop,
                  Strings.LeftRunningStopAll, Strings.LeftRunningNoneNow, jobs);

    /// <summary>The sentence an editor without a card (ACP) says after the answer; <c>null</c> when nothing runs.</summary>
    public static string? Text(List<LeftRunningJob> jobs) =>
        jobs.Count == 0 ? null
            : Strings.LeftRunningText(jobs.Count, string.Join(", ", jobs.Select(j => $"{Code(j.Command)} ({j.Id})")));

    /// <summary>What a Stop did, as the row then says it; <c>Done</c> when the job no longer runs. A job the kill did not end
    /// is not done: its row keeps Stop, and it still counts as running.</summary>
    public static (string Text, bool Done) Outcome((Shell.BackgroundShellRegistry.StopOutcome Outcome, int? ExitCode) result) => result.Outcome switch
    {
        Shell.BackgroundShellRegistry.StopOutcome.Stopped    => (Strings.LeftRunningStopped, true),
        Shell.BackgroundShellRegistry.StopOutcome.NotStopped => (Strings.LeftRunningNotStopped, false),
        _ => (result.ExitCode is { } code ? Strings.LeftRunningEnded(code) : Strings.LeftRunningEndedUnknown, true),
    };

    /// <summary>A command as an inline code span, fenced longer than any backtick run it holds (a PowerShell escape).</summary>
    private static string Code(string command)
    {
        var longest = MarkdownParser.LongestBacktickRun(command);
        if (longest == 0) return $"`{command}`";
        var fence = new string('`', longest + 1);
        return $"{fence} {command} {fence}";
    }

    /// <summary>A command on one line: its lines joined, cut past <see cref="MaxCommandChars"/>.</summary>
    internal static string OneLine(string command)
    {
        var line = string.Join(" ⏎ ", command.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return line.Length <= MaxCommandChars ? line : line[..(MaxCommandChars - 1)] + "…";
    }
}
