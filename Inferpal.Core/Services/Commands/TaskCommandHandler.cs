using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure logic of <c>/task</c> — detached agent runs (ROADMAP §9). The handler owns the routing and
/// the rendering; the queue owns the lifecycle, and the front-ends only supply the runner and
/// display what comes back. Shared by VS and VS Code, like every other slash command.
/// </summary>
/// <remarks>
/// Sub-commands: <c>/task &lt;objective&gt;</c> submits, <c>/task</c> or <c>/task list</c> lists,
/// <c>/task &lt;id&gt;</c> shows one report, <c>/task stop &lt;id&gt;</c> cancels, <c>/task clear</c>
/// forgets finished ones. An argument is read as an id only when it looks like one *and* names a
/// known task — otherwise "t1 is broken, investigate" would be swallowed as a lookup instead of
/// being submitted as the objective it obviously is.
/// </remarks>
internal static class TaskCommandHandler
{
    /// <summary>Outcome of a <c>/task</c> invocation: the markdown to display.</summary>
    /// <param name="Message">Markdown to display.</param>
    /// <param name="Apply">
    /// A proposal the front-end must apply (roadmap §18). The handler never writes: applying goes
    /// through the real tools, so it gets the ordinary approval prompt, the snapshot and
    /// <c>/undo-run</c> coverage — which is exactly why the handler cannot do it itself.
    /// </param>
    internal readonly record struct TaskCommandResult(string Message, TaskProposal? Apply = null);

    /// <param name="queue">The workspace's background queue.</param>
    /// <param name="parts">Tokenised command, <c>parts[0]</c> being <c>/task</c>.</param>
    internal static TaskCommandResult Handle(BackgroundTaskQueue queue, string[] parts)
    {
        var args = parts.Length > 1 ? parts[1..] : [];

        if (args.Length == 0 || IsWord(args[0], "list"))
            return new(RenderList(queue));

        // `/task propose <objective>`: the task may express changes, none of them applied.
        if (IsWord(args[0], "propose"))
        {
            var goal = string.Join(" ", args[1..]).Trim();
            if (goal.Length == 0) return new(Strings.SlashUsage("/task propose <objective>"));

            var proposedId = queue.Submit(goal, proposeWrites: true);
            return new(proposedId is null
                ? Strings.TaskQueueFull(BackgroundTaskQueue.MaxPending)
                : Strings.TaskSubmittedProposing(proposedId, goal));
        }

        // `/task apply <id> <n>`: one proposal, through the usual prompt. There is deliberately no
        // form that applies them all — that would be the grouped approval §9 refuses, moved from
        // submission to return.
        if (IsWord(args[0], "apply"))
            return Apply(queue, args);

        if (IsWord(args[0], "stop") || IsWord(args[0], "cancel"))
        {
            if (args.Length < 2) return new(Strings.SlashUsage("/task stop <id>"));
            var id = args[1];
            return new(queue.Cancel(id) ? Strings.TaskStopRequested(id) : Strings.TaskUnknown(id));
        }

        if (IsWord(args[0], "clear"))
            return new(Strings.TaskForgot(queue.ClearFinished()));

        // A lone, existing id reads as "show me that one".
        if (args.Length == 1 && queue.Get(args[0]) is { } known)
            return new(RenderReport(known));

        var objective = string.Join(" ", args).Trim();
        var newId = queue.Submit(objective);
        return new(newId is null
            ? Strings.TaskQueueFull(BackgroundTaskQueue.MaxPending)
            : Strings.TaskSubmitted(newId, objective));
    }

    private static bool IsWord(string arg, string word) =>
        string.Equals(arg, word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves <c>/task apply &lt;id&gt; &lt;n&gt;</c> to the one proposal it names.</summary>
    private static TaskCommandResult Apply(BackgroundTaskQueue queue, string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[2], out var number))
            return new(Strings.SlashUsage("/task apply <id> <n>"));

        if (queue.Get(args[1]) is not { } task) return new(Strings.TaskUnknown(args[1]));

        var proposals = task.PendingProposals;
        if (proposals.Count == 0) return new(Strings.TaskNoProposals(task.Id));

        if (number < 1 || number > proposals.Count)
            return new(Strings.TaskProposalUnknown(number, proposals.Count));

        // The front-end applies it and reports; the handler only names which one.
        return new(string.Empty, proposals[number - 1]);
    }

    // ── Rendering ───────────────────────────────────────────────────────────────

    /// <summary>Compact table of every known task.</summary>
    internal static string RenderList(BackgroundTaskQueue queue)
    {
        var tasks = queue.List();
        if (tasks.Count == 0) return Strings.TaskListEmpty;

        var sb = new StringBuilder();
        sb.AppendLine($"**{Strings.TaskListTitle}**").AppendLine();
        sb.AppendLine($"| # | {Strings.TaskColumnState} | {Strings.TaskColumnObjective} |");
        sb.AppendLine("|---|---|---|");

        foreach (var task in tasks)
        {
            var state = StateLabel(task);
            sb.AppendLine($"| `{task.Id}` | {state} | {Escape(Truncate(task.Objective, 70))} |");
        }

        sb.AppendLine().AppendLine(Strings.TaskListHint);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Full report of one task — what a background run is ultimately for.</summary>
    internal static string RenderReport(BackgroundTaskSnapshot task)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**`{task.Id}`** — {StateLabel(task)}").AppendLine();
        sb.AppendLine($"> {Escape(task.Objective)}").AppendLine();

        if (task.Duration is { } duration)
            sb.AppendLine(Strings.TaskDuration(FormatDuration(duration))).AppendLine();

        if (!string.IsNullOrWhiteSpace(task.Error))
            sb.AppendLine(Strings.MsgError(task.Error!)).AppendLine();

        if (!string.IsNullOrWhiteSpace(task.Result))
            sb.AppendLine(task.Result!.Trim());
        else if (!task.IsFinished)
            sb.AppendLine(Strings.TaskStillRunning);

        if (task.PendingProposals.Count > 0)
            sb.AppendLine(TaskProposalReport.Render(task.Id, task.PendingProposals));

        // The journal explains an empty or surprising report; only useful while/after running.
        if (task.Steps.Count > 0)
        {
            sb.AppendLine().AppendLine("<details>");
            sb.AppendLine($"<summary>{Strings.TaskStepsTitle} ({task.Steps.Count})</summary>").AppendLine();
            foreach (var step in task.Steps) sb.AppendLine($"- {Escape(step)}");
            sb.AppendLine().AppendLine("</details>");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Localized state, with the queue position while waiting ("queued (2nd)").</summary>
    internal static string StateLabel(BackgroundTaskSnapshot task) => task.State switch
    {
        BackgroundTaskState.Queued    => task.QueuePosition > 0
                                             ? Strings.TaskStateQueuedAt(task.QueuePosition)
                                             : Strings.TaskStateQueued,
        BackgroundTaskState.Running   => Strings.TaskStateRunning,
        BackgroundTaskState.Succeeded => Strings.TaskStateSucceeded,
        BackgroundTaskState.Failed    => Strings.TaskStateFailed,
        _                             => Strings.TaskStateCancelled,
    };

    internal static string FormatDuration(TimeSpan d) =>
        d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{d.TotalSeconds:0.0}s";

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>Table cells and quotes are markdown: a pipe or newline in the objective would
    /// otherwise break the row it sits in.</summary>
    private static string Escape(string text) =>
        text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
