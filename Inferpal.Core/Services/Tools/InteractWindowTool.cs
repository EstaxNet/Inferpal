using System.Text;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Shell;

namespace Inferpal.Services.Tools;

/// <summary>
/// Agent tool: uses the desktop app the agent launched itself the way a person does — typing in a field, a click on a
/// button, a choice in a list, a box ticked — then looks at the state reached, as <c>view_window</c> does, and can save its
/// picture into the project (pari 126).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Only the windows of the job's process tree (<see cref="WindowActions"/> and <see cref="WindowCapture"/> choose them by
/// their owner's process id): never another window of the machine. The person agreed to the launch on its card; acting in
/// what was launched is the agent's own work, with no card of its own — approving each click would defeat the help it
/// gives. The person's <c>deny</c> rules are read at every call, on the job's command.
/// </para>
/// <para>
/// ⚠ Through the system's accessibility (UI Automation, AT-SPI, macOS Accessibility), never the person's mouse or keyboard:
/// what they type goes to the window they are in. A file dialog is refused: it opens the file system, which no step chooses.
/// </para>
/// <para>
/// ⚠ The steps stop at the first one not done; the model continues from the state the result describes.
/// </para>
/// </remarks>
internal sealed class InteractWindowTool : ITool
{
    private readonly BackgroundShellRegistry _jobs;
    private readonly ToolImageInbox _images;
    private readonly IApprovalService _approval;
    private readonly Func<string> _getWorkspaceRoot;
    private readonly FileHistoryService? _history;
    private readonly Func<int, IReadOnlyList<WindowStep>, TimeSpan, Action<int, WindowStepResult>, CancellationToken, Task<WindowActing>> _play;
    private readonly Func<int, TimeSpan, CancellationToken, Task<WindowReading>> _read;

    /// <param name="history">The run's history, which backs a saved picture up first; without it, <c>save_to</c> is
    /// refused.</param>
    public InteractWindowTool(BackgroundShellRegistry jobs, ToolImageInbox images, IApprovalService approval,
                              Func<string> getWorkspaceRoot, FileHistoryService? history = null,
                              Func<int, IReadOnlyList<WindowStep>, TimeSpan, Action<int, WindowStepResult>, CancellationToken, Task<WindowActing>>? play = null,
                              Func<int, TimeSpan, CancellationToken, Task<WindowReading>>? read = null)
    {
        _jobs = jobs;
        _images = images;
        _approval = approval;
        _getWorkspaceRoot = getWorkspaceRoot;
        _history = history;
        _play = play ?? ((pid, steps, wait, onStep, ct) => WindowActions.PlayAsync(pid, steps, wait, ct, onStep));
        _read = read ?? WindowCapture.ReadAsync;
    }

    public string Name => "interact_window";

    /// <inheritdoc/>
    /// <remarks>⚠ Offered only while a background job runs, as <c>view_window</c>: every definition goes with every request.</remarks>
    public bool IsOffered => _jobs.List().Any(j => j.Running);

    /// <inheritdoc/>
    /// <remarks>A click may make the app do anything a person's would (save a file, send a form); with <c>save_to</c>, it
    /// writes the picture into the workspace.</remarks>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Runs | (args.Has("save_to") ? ToolEffect.Writes : ToolEffect.None);

    internal const int MaxSteps = 25, MaxWaitMs = 10_000, DefaultWaitMs = 5_000;

    /// <summary>How long the steps look after the state they reached for the window to show it.</summary>
    private static readonly TimeSpan LookAfter = TimeSpan.FromSeconds(5);

    // ⚠ Short on purpose: every tool's definition goes with every request, and at an 8K window they fill most of it.
    public string Description =>
        "Use the desktop app you launched with run_command (background: true) as a person does: type in a field, click, choose " +
        "in a list, tick a box, wait — then see its windows as view_window does. A control is its id or name from that tree. " +
        "save_to writes the picture of its window into the project as a PNG.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            job = new { type = "string", description = "The app's background job (bg1)." },
            save_to = new { type = "string", description = "A PNG file of the project to save the window's picture to (docs/images/order.png)." },
            wait = new { type = "integer", description = $"Seconds to wait for a first window (default {ViewWindowTool.DefaultWaitSeconds})." },
            steps = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { type = "string", @enum = WindowActions.Actions },
                        control = new { type = "string", description = "The control: its #id or its \"name\" in the tree" },
                        text = new { type = "string", description = "type: the text" },
                        item = new { type = "string", description = "select: the entry to choose" },
                        timeout_ms = new { type = "integer", description = $"wait_for: at most {MaxWaitMs}" },
                    },
                    required = new[] { "action", "control" },
                },
            },
        },
        required = new[] { "job", "steps" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var job = args.Trimmed("job");
        if (string.IsNullOrEmpty(job)) return ToolFailure.Said("Name the background job that runs the app: job=\"bg1\".");
        var (steps, notes, stepsError) = ReadSteps(args);
        if (stepsError is not null) return "Error: " + stepsError;
        var (wait, clampNote) = ClampedArgument.Read(args, "wait", ViewWindowTool.DefaultWaitSeconds, 0, ViewWindowTool.MaxWaitSeconds);
        if (clampNote is not null) notes.Insert(0, clampNote);
        // Checked before any step plays: a call never acts for a picture it cannot save.
        var root = _getWorkspaceRoot();
        var (saveTo, saveRefusal) = CaptureSaving.Target(args.Str("save_to"), root);
        if (saveRefusal is not null) return saveRefusal;
        if (saveTo is not null && _history is null) return "Error: save_to is not available here: the picture cannot be saved.";
        if (saveTo is not null && CaptureSaving.Unseen(_history!, root, saveTo) is { } unseen)
            return "Error: save_to: " + unseen + " Nothing was played.";
        if (saveTo is not null && _approval.Refuses("write_file", saveTo))
            return $"Error: save_to: the person's rules refuse writing '{saveTo}'. Nothing was played.";

        var (pid, missing) = ViewWindowTool.Locate(_jobs, job);
        if (missing is not null) return missing;
        // The person's rules, at every call, on the command that started the app.
        var command = _jobs.List().FirstOrDefault(j => j.Id == job).Command ?? job;
        if (_approval.Refuses(Name, command)) return ToolFailure.CancelledByUser;

        var lines = new List<string>(notes);
        var saveName = saveTo is null ? null : Path.GetRelativePath(root, saveTo).Replace('\\', '/');
        // Each step on the run's status line as it ends: the person sees the app being used, not a call that waits.
        var acting = await _play(pid, steps!, TimeSpan.FromSeconds(wait),
            (i, step) => ToolProgress.Step(Name, i, steps!.Count, step.Done, step.Detail),
            ct);
        if (acting.Refusal is { } refusal && !acting.MayHavePlayed && acting.Steps.Count == 0)
        {
            lines.Add($"The steps could not be played in the windows of {job}: {refusal}.");
            if (saveName is not null) lines.Add(CaptureSaving.NotSavedPrefix + saveName + ": no step was played.");
            return string.Join("\n", lines);
        }
        // Each step reported, as it ended.
        var reported = Math.Min(acting.Steps.Count, steps!.Count);
        for (var i = 0; i < reported; i++)
        {
            var (step, result) = (steps[i], acting.Steps[i]);
            lines.Add($"{i + 1}. {step.Action} {step.Target}: {(result.Done ? "done" : "NOT DONE")} — {result.Detail}");
        }
        var allDone = acting.Refusal is null && reported == steps.Count && acting.Steps.All(s => s.Done);
        if (acting.Refusal is { } stopped)
        {
            // ⚠ Never "nothing was played" when a step may have run (a form sent): playing it again would repeat it.
            lines.Add(acting.MayHavePlayed
                ? $"The steps stopped after step {reported}: {stopped}. Step {reported + 1} may have been played: what the window "
                  + "shows now follows — read it before playing any step again."
                : $"The steps stopped after step {reported}: {stopped}.");
        }
        else if (reported < steps.Count)
            lines.Add($"Steps {reported + 1}-{steps.Count} not played: they were written for a state the window did not reach.");
        if (!allDone && saveTo is not null)
        {
            lines.Add(CaptureSaving.NotSavedPrefix + saveName + ": the steps did not all report done, so the window is not in the "
                      + "state asked for.");
            saveTo = null;
        }

        var reading = await _read(pid, LookAfter, ct);
        if (reading.Refusal is { } blind)
        {
            lines.Add($"The windows of {job} could not be looked at after the steps: {blind}.");
            if (saveTo is not null) lines.Add(CaptureSaving.NotSavedPrefix + saveName + ": there is no picture of the window.");
            return string.Join("\n", lines);
        }
        var sb = new StringBuilder(string.Join("\n", lines)).AppendLine();
        ViewWindowTool.Render(job, (int)LookAfter.TotalSeconds, reading, _images, sb);
        if (saveTo is not null)
        {
            // The largest window with a picture: the app's own, never a message box it shows over it (the systems list the
            // windows front first).
            if (reading.Windows.Where(w => w.Png is not null).MaxBy(w => (long)w.Width * w.Height) is { Png: { } png } window)
            {
                var title = string.IsNullOrEmpty(window.Title) ? "(untitled)" : window.Title;
                sb.AppendLine().Append($"Saving the picture of \"{title}\": ")
                  .Append(await CaptureSaving.SaveAsync(_approval, _history!, root, saveTo, png, ct));
            }
            else sb.AppendLine().Append(CaptureSaving.NotSavedPrefix + saveName + ": no window of the app gave a picture.");
        }
        return sb.ToString().TrimEnd();
    }

    /// <returns>The steps, the notes of a clamped wait (said above the report), or the error.</returns>
    private static (List<WindowStep>? Steps, List<string> Notes, string? Error) ReadSteps(JsonElement args)
    {
        var notes = new List<string>();
        if (!args.Array("steps", out var array)) return (null, notes, "'steps' must be an array of step objects.");
        var count = array.GetArrayLength();
        if (count == 0) return (null, notes, "no step: to look at the app without acting, call view_window.");
        if (count > MaxSteps)
            return (null, notes, $"{count} steps: one call plays {MaxSteps} at most. Split them over several calls; the app stays open.");
        var steps = new List<WindowStep>();
        var index = 0;
        foreach (var e in array.EnumerateArray())
        {
            index++;
            if (e.ValueKind != JsonValueKind.Object) return (null, notes, $"step {index} is not an object.");
            var action = e.Keyword("action");
            if (action is null || !WindowActions.Actions.Contains(action))
                return (null, notes, $"step {index}: unknown action '{e.Str("action")}' ({string.Join(", ", WindowActions.Actions)}).");
            // The tree writes an id as #id and a name in quotes: either is taken as the tree writes it.
            var control = e.Str("control")?.Trim().TrimStart('#').Trim('"');
            if (string.IsNullOrWhiteSpace(control)) return (null, notes, $"step {index} ({action}) needs a control: its #id or \"name\" in the tree.");
            var text = e.Str("text");
            var item = e.Str("item");
            if (action == "type" && text is null) return (null, notes, $"step {index} (type) needs the text to type.");
            if (action == "select" && string.IsNullOrEmpty(item)) return (null, notes, $"step {index} (select) needs the entry to choose (item).");
            var (timeout, note) = ClampedArgument.Read(e, "timeout_ms", DefaultWaitMs, 0, MaxWaitMs);
            if (note is not null) notes.Add($"Step {index}: {note}");
            steps.Add(new WindowStep(action, control, text, item, timeout));
        }
        return (steps, notes, null);
    }
}
