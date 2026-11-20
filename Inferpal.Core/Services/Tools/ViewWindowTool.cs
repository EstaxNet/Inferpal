using System.Text;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Shell;

namespace Inferpal.Services.Tools;

/// <summary>
/// Agent tool: looks at the desktop app the agent launched itself (pari 102) — a background job of <c>run_command</c> and the
/// processes it started — as a picture of its window and as its accessibility tree (role, name, state, value, place). What the
/// source does not show is there: a button greyed out, a field left empty by a binding, a control outside the window, a banner
/// over a button.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Only the windows of the job's process tree (<see cref="WindowCapture"/> chooses them by their owner's process id): never
/// another window of the machine — a mail, a password manager. The person agreed to the launch on its card; looking at what was
/// launched is a read, with no card of its own.
/// </para>
/// <para>
/// The picture reaches the model the way <c>read_file</c>'s images do (<see cref="ToolImages"/>): as a picture for a model that
/// sees, as the text OCR read on it for one that does not. The tree goes to every model: measured, it lets a model that does not
/// see find a disabled button, an empty field and a control out of the window.
/// </para>
/// </remarks>
internal sealed class ViewWindowTool : ITool
{
    private readonly BackgroundShellRegistry _jobs;
    private readonly ToolImageInbox _images;
    private readonly Func<int, TimeSpan, CancellationToken, Task<WindowReading>> _read;

    public ViewWindowTool(BackgroundShellRegistry jobs, ToolImageInbox images,
                          Func<int, TimeSpan, CancellationToken, Task<WindowReading>>? read = null)
    {
        _jobs = jobs;
        _images = images;
        _read = read ?? WindowCapture.ReadAsync;
    }

    public string Name => "view_window";

    /// <inheritdoc/>
    /// <remarks>⚠ Offered only while a background job runs: with none there is nothing to look at, and every definition goes
    /// with every request — at an 8K window the tools already fill most of it.</remarks>
    public bool IsOffered => _jobs.List().Any(j => j.Running);

    /// <inheritdoc/>
    /// <remarks>Inferpal's own helper reads the screen and the controls, as <c>ocr</c>'s reads a picture: nothing of the
    /// person's runs, nothing of the app changes.</remarks>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Reads;

    /// <summary>How long a first window is waited for, by default and at most.</summary>
    internal const int DefaultWaitSeconds = 20, MaxWaitSeconds = 60;

    /// <summary>The most characters of trees one answer gives, all windows together.</summary>
    internal const int MaxTreeChars = 12_000;

    // ⚠ Short on purpose: every tool's definition goes with every request, and at an 8K window they fill most of it.
    public string Description =>
        "See the desktop app you launched with run_command (background: true): a picture of its windows and each control's " +
        "role, name, value, state and place — what the source does not show (a greyed button, an empty field, a hidden control).";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            job  = new { type = "string",  description = "The app's background job (bg1)." },
            wait = new { type = "integer", description = $"Seconds to wait for a window (default {DefaultWaitSeconds})." },
        },
        required = new[] { "job" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var job = args.Trimmed("job");
        if (string.IsNullOrEmpty(job)) return ToolFailure.Said("Name the background job that runs the app: job=\"bg1\".");
        var (wait, clampNote) = ClampedArgument.Read(args, "wait", DefaultWaitSeconds, 0, MaxWaitSeconds);
        var (pid, missing) = Locate(_jobs, job);
        if (missing is not null) return missing;

        var reading = await _read(pid, TimeSpan.FromSeconds(wait), ct);
        if (reading.Refusal is { } refusal) return $"The windows of {job} could not be looked at: {refusal}.";
        var sb = new StringBuilder();
        if (clampNote is not null) sb.AppendLine(clampNote);
        Render(job, wait, reading, _images, sb);
        return sb.ToString().TrimEnd();
    }

    /// <summary>The process of a running background job, or what to say when there is none — one reader for looking at a
    /// window and acting in it.</summary>
    internal static (int Pid, string? Missing) Locate(BackgroundShellRegistry jobs, string job)
    {
        // A run on a copy reaches the apps it started there only: one started in the person's folder is theirs (as run_command).
        bool Ours(string id) => Execution.RunRoot.Copy is not { } inCopy
                                || jobs.FolderOf(id) is not { } folder || Execution.RunRoot.Holds(inCopy, folder);
        if (!Ours(job))
            return (0, $"Error: background job '{job}' runs in the user's own folder, and this run works in a copy of the "
                     + "repository — not available here.");
        if (jobs.ProcessIdOf(job) is { } pid) return (pid, null);
        // A shell that ended at once usually started the app detached (an .exe by its path, Start-Process, start, open -a,
        // a trailing &): the app runs, out of the job's tree, where these tools never look.
        if (jobs.List().Any(j => j.Id == job))
            return (0, $"The shell of {job} has ended: if it started the app, the app was detached from it, and this tool only looks "
                     + "at the processes of the job. Launch the app so it stays the job's child — in PowerShell `& .\\App.exe | Out-Null`, "
                     + "in a POSIX shell `./app` without `&`, `dotnet run` as it is — then try again.");
        var running = jobs.List().Where(j => j.Running && Ours(j.Id)).Select(j => $"{j.Id} ({j.Command})").ToList();
        return (0, $"No running background job '{job}'. " + (running.Count == 0
            ? "Launch the app with run_command and background: true, then try again."
            : "Running: " + string.Join(", ", running) + "."));
    }

    /// <summary>The windows of a reading, each with its picture (put in <paramref name="images"/>) and its tree, the trees
    /// within <see cref="MaxTreeChars"/> together, then the reading's notes.</summary>
    internal static void Render(string job, int wait, WindowReading reading, ToolImageInbox images, StringBuilder sb)
    {
        if (reading.Windows.Count == 0)
        {
            // Its output is not read here: a poll would take it from the model's own next poll of the job.
            sb.Append($"{job} shows no window after {wait} s. Read what it printed with run_command (action poll, id {job}): an "
                    + "error, or an app still starting (look again with a longer wait), or an app that has no window.");
            foreach (var note in reading.Notes) sb.Append('\n').Append(note);
            return;
        }

        var treeBudget = MaxTreeChars;
        foreach (var w in reading.Windows)
        {
            var name = string.IsNullOrEmpty(w.Title) ? "(untitled)" : w.Title;
            if (w.FileDialog)
            {
                sb.AppendLine($"## Window \"{name}\" — process {w.ProcessId}: a file dialog. Its contents, the person's folders and "
                            + "files, are not shown, and no step acts in it. While it is open the app may wait on it: stop the job "
                            + $"(run_command action stop, id {job}) and launch the app again.");
                continue;
            }
            sb.Append($"## Window \"{name}\" — process {w.ProcessId}");
            if (w.Png is { } png && ChatImages.Read(png, $"{name}.png").Image is { } image)
            {
                images.Put(image);
                sb.Append($", {w.Width}×{w.Height}: its picture follows this result");
            }
            else sb.Append(", no picture");
            sb.AppendLine();
            if (w.Tree.Count == 0) { sb.AppendLine("(no accessibility tree)"); continue; }
            sb.AppendLine("Accessibility tree — role \"name\" #id state [x,y width×height], relative to the window:");
            var text = string.Join("\n", w.Tree);
            if (text.Length > treeBudget)
            {
                var cut = text.LastIndexOf('\n', Math.Max(0, treeBudget - 1));
                sb.AppendLine(text[..Math.Max(0, cut)]).AppendLine($"… (tree cut: {text.Length - Math.Max(0, cut)} more characters not shown)");
                treeBudget = 0;
            }
            else
            {
                sb.AppendLine(text);
                treeBudget -= text.Length;
            }
        }
        foreach (var note in reading.Notes) sb.AppendLine(note);
    }
}
