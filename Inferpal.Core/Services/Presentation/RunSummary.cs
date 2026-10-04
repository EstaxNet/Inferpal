using System.Globalization;
using Inferpal.Localization;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Presentation;

/// <summary>What the last check of a run said: the build or the tests, passed or failed; none when nothing checked.</summary>
internal enum RunCheck { None, BuildPassed, BuildFailed, TestsPassed, TestsFailed }

/// <summary>One file a run changed, as its result bar shows it.</summary>
/// <param name="Added">Lines added since the run's backup — an estimate, never a promise of git's count.</param>
/// <param name="Created">The run created the file (undoing the run deletes it).</param>
/// <param name="Gone">The file no longer exists.</param>
internal sealed record RunFileLine(string Path, string Name, int Added, int Removed, bool Created, bool Gone);

/// <summary>
/// The run of one agent turn, as the chat shows it under the answer: one collapsible line for its steps ("3 steps ·
/// read 1 file · edited 1 · build passed") and a result bar — the files it changed, the last check, Undo run.
/// </summary>
/// <param name="RunId">The run <c>/undo-run</c> takes; empty when the run changed nothing.</param>
internal sealed record RunSummaryModel(
    int Steps, string Title, string Detail, IReadOnlyList<RunFileLine> Files, RunCheck Check, string CheckText,
    string RunId);

/// <summary>
/// Builds <see cref="RunSummaryModel"/> from the tool calls of a turn and the file history of its run — the same
/// summary in both editors.
/// </summary>
internal static class RunSummary
{
    /// <summary>The tools that look something up without changing anything: they count as searches.</summary>
    private static readonly HashSet<string> Searches = new(StringComparer.Ordinal)
    {
        "search_in_files", "search_codebase", "search_docs", "list_files", "generate_project_map", "analyze_code",
        "web_search", "fetch_url", "get_git_status", "get_solution_info",
    };

    /// <summary>The summary of a turn; <c>null</c> when no tool ran (an answer alone has no run to show).</summary>
    /// <param name="run">The file-history run of the turn, or <c>null</c> when it has none (nothing was tracked).</param>
    public static RunSummaryModel? Build(IReadOnlyList<ToolExecution> executions, HistoryRun? run)
    {
        if (executions.Count == 0) return null;

        var reads    = executions.Count(e => e.Name == "read_file");
        var searches = executions.Count(e => Searches.Contains(e.Name));
        var commands = executions.Count(e => e.Name == "run_command");
        var files    = run is null ? [] : Files(run);
        var check    = LastCheck(executions);

        var parts = new List<string>();
        if (reads > 0)       parts.Add(reads == 1 ? Strings.RunRead1 : Strings.RunRead(reads));
        if (searches > 0)    parts.Add(searches == 1 ? Strings.RunSearched1 : Strings.RunSearched(searches));
        if (files.Count > 0) parts.Add(files.Count == 1 ? Strings.RunEdited1 : Strings.RunEdited(files.Count));
        if (commands > 0)    parts.Add(commands == 1 ? Strings.RunCommand1 : Strings.RunCommands(commands));
        var checkText = CheckText(check);
        if (checkText.Length > 0) parts.Add(checkText);

        return new RunSummaryModel(
            executions.Count,
            executions.Count == 1 ? Strings.RunSteps1 : Strings.RunSteps(executions.Count),
            string.Join(" · ", parts),
            files, check, checkText,
            files.Count > 0 ? run!.Id : string.Empty);
    }

    /// <summary>The words of a check, empty for none.</summary>
    public static string CheckText(RunCheck check) => check switch
    {
        RunCheck.BuildPassed => Strings.RunBuildPassed,
        RunCheck.BuildFailed => Strings.RunBuildFailed,
        RunCheck.TestsPassed => Strings.RunTestsPassed,
        RunCheck.TestsFailed => Strings.RunTestsFailed,
        _                    => string.Empty,
    };

    /// <summary>
    /// The last check of the run, read the way <see cref="ChatTurnPolicy.LastCheckFailed"/> reads it — a build
    /// (<c>get_diagnostics</c>), the tests (<c>run_tests</c>), or the build check an edit ran — and passed only on a
    /// verdict that says so: a run where nothing was proven passes nothing.
    /// </summary>
    internal static RunCheck LastCheck(IReadOnlyList<ToolExecution> executions)
    {
        var last = executions.LastOrDefault(e => e.Name is "run_tests" or "get_diagnostics"
                                              || (ChatTurnPolicy.IsFileEdit(e.Name)
                                                  && CodeActions.SmartFixValidator.ReadVerdict(e.Output) is not null));
        return last switch
        {
            null => RunCheck.None,
            { Name: "get_diagnostics" } => Tools.GetDiagnosticsTool.ReadVerdict(last.Output) switch
            {
                Tools.GetDiagnosticsTool.BuildVerdict.Clean  => RunCheck.BuildPassed,
                Tools.GetDiagnosticsTool.BuildVerdict.Errors => RunCheck.BuildFailed,
                _                                            => RunCheck.None,
            },
            { Name: "run_tests" } => Commands.TddCommandHandler.TestsFailed(last.Output) ? RunCheck.TestsFailed
                                   : Commands.TddCommandHandler.TestsPassed(last.Output) ? RunCheck.TestsPassed
                                   : RunCheck.None,
            _ => CodeActions.SmartFixValidator.ReadVerdict(last.Output) == true ? RunCheck.BuildFailed : RunCheck.BuildPassed,
        };
    }

    /// <summary>The files the run changed, each with its lines added and removed since the run's backup.</summary>
    internal static IReadOnlyList<RunFileLine> Files(HistoryRun run) =>
        [.. run.Changes.OrderBy(c => c.OriginalPath, StringComparer.OrdinalIgnoreCase).Select(change =>
        {
            var path    = change.OriginalPath;
            var exists  = File.Exists(path);
            var created = change.SnapshotPath is null && !change.SnapshotFailed;
            var before  = change.SnapshotPath is { } snap && File.Exists(snap) ? Read(snap) : string.Empty;
            var after   = exists ? Read(path) : string.Empty;
            var (added, removed) = LineDelta(before, after);
            return new RunFileLine(path, System.IO.Path.GetFileName(path), added, removed, created, !exists);
        })];

    private static string Read(string path)
    {
        try { return Tools.TextFileEncoding.ReadText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    /// <summary>
    /// Lines added and removed between two texts: the common head and tail set aside, then what is left on each side
    /// that the other side does not hold. An estimate — a moved line counts as kept — never shown as git's count.
    /// </summary>
    internal static (int Added, int Removed) LineDelta(string before, string after)
    {
        var a = before.Length == 0 ? [] : before.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var b = after.Length  == 0 ? [] : after.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[^(tail + 1)] == b[^(tail + 1)]) tail++;

        var left = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = head; i < a.Length - tail; i++) left[a[i]] = left.GetValueOrDefault(a[i]) + 1;
        var added = 0;
        for (var i = head; i < b.Length - tail; i++)
        {
            if (left.TryGetValue(b[i], out var n) && n > 0) left[b[i]] = n - 1;
            else added++;
        }
        return (added, left.Values.Sum());
    }

    /// <summary>The argument names a step acts on, in the order a step line looks for them.</summary>
    private static readonly string[] SubjectKeys = ["path", "file_path", "command", "query", "url", "symbol", "pattern"];

    /// <summary>
    /// What a step acted on, as its line in the run shows it: the path, command, query or address its arguments name;
    /// empty when they name none (or are not JSON — a custom tool's raw arguments: its name alone says enough).
    /// </summary>
    public static string Subject(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return string.Empty;
            foreach (var key in SubjectKeys)
            {
                if (doc.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text)
                {
                    text = text.ReplaceLineEndings(" ");
                    return text.Length > 80 ? text[..80] + "…" : text;
                }
            }
        }
        catch (System.Text.Json.JsonException) { }
        return string.Empty;
    }

    /// <summary>How long a turn took, as its header shows it: "21 s", "2 min 05 s".</summary>
    public static string Duration(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60
            ? Strings.TurnSeconds(Math.Max(0, (int)Math.Round(elapsed.TotalSeconds)))
            : Strings.TurnMinutes((int)elapsed.TotalMinutes, elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture));
}
