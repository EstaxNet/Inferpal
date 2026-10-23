using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.CodeActions;

/// <summary>
/// "Explain the Failure" on a test in VS Code's Test view: the failure — the message the editor shows, or the report of
/// the test run again — and the code around the test and the failing line, asked as a code action without tools. A test
/// that passes, or a run that is no failing verdict (no runner, nothing ran, stopped at its budget, a tool error), is
/// said instead.
/// </summary>
/// <remarks>Visual Studio's Test Explorer publishes no selection to an extension: this gesture is VS Code's alone.</remarks>
internal static class TestFailureExplain
{
    private static readonly FixPromptBuilder.SiteWords TestSite =
        new("Code around the test:", "test", Reader: null, WholeWhenItFits: false);

    /// <param name="label">The test's name, as the Test view shows it.</param>
    /// <param name="filter">The name <paramref name="runTests"/> selects the test by: its label without its parameters.</param>
    /// <param name="failure">The failure message the editor holds; <c>null</c> to run the test (<paramref name="runTests"/>).</param>
    /// <param name="test">The test's file and 1-based line, when the editor knows them.</param>
    /// <param name="failedAt">Where the failure message points, when it does.</param>
    /// <param name="runTests">Runs the test (<see cref="RunTestAsync"/>) and gives the report.</param>
    internal static async Task<ExplainAsk> ForAsync(string label, string filter, string? failure,
        (string? File, int Line) test, (string? File, int Line) failedAt, Func<CancellationToken, Task<string>> runTests,
        string? rootDir, Editor.OpenDocumentOverlay? overlay, int budgetChars, CancellationToken ct)
    {
        string report;
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(failure)) report = failure.Trim();
        else
        {
            var output = await runTests(ct);
            if (Commands.TddCommandHandler.NotAVerdict(output) is { } notAVerdict) return Refused(notAVerdict);
            if (Commands.TddCommandHandler.TestsPassed(output)) return Refused(Strings.TestPasses(label));
            // Neither green nor red — a tool error, a timeout of the call: no failure to explain, said as it is.
            if (!Commands.TddCommandHandler.TestsFailed(output)) return Refused(output.Trim());
            report = output.Trim();
            // The filter is a name, matched by each runner its own way (a substring, a pattern): the run can take in
            // other tests, and a red report that never names this one is about them.
            if (!RegexBudget.IsMatch(new Regex($@"(?<!\w){Regex.Escape(filter)}(?!\w)", RegexOptions.None, RegexBudget.Default), report))
                notes.Add($"this report does not name it: the failures below are other tests that the name '{filter}' selected");
        }

        var sites = new List<(string Path, int Line)>();
        foreach (var (file, line) in new[] { test, failedAt })
        {
            if (string.IsNullOrEmpty(file) || line <= 0) continue;
            if (Tools.PathSanitizer.IsUnderRoot(Path.GetFullPath(file), rootDir)) sites.Add((file, line));
            else notes.Add($"{Path.GetFileName(file)} is outside the workspace: not read");
        }
        var code = string.Empty;
        if (sites.Count > 0)
        {
            if (overlay is not null) await overlay.RefreshAsync(ct, [.. sites.Select(s => s.Path).Distinct(PathComparer.Default)]);
            code = FixPromptBuilder.CodeAround(sites, FixPromptBuilder.AsTheEditorHasIt(overlay), TestSite);
        }
        // Half the budget at most for the code, the rest for the failure: a long stack trace never pushes the test out.
        var heading    = notes.Count == 0 ? $"Test: {label}" : $"Test: {label} ({string.Join("; ", notes.Distinct())})";
        var codePart   = CodeExcerpt.Of(code, budgetChars / 2);
        var reportPart = CodeExcerpt.Of($"{heading}\n\n{report}", budgetChars - codePart.Text.Length);
        var content    = codePart.Text.Length > 0 ? reportPart.Text + "\n\n" + codePart.Text : reportPart.Text;
        var chip       = (reportPart.IsTruncated ? reportPart : codePart).Label(Strings.TestFailureChip(label));
        return new(Strings.ExplainTestFailureQuestion(label), chip, content);
    }

    /// <summary>
    /// Runs the clicked test with <c>run_tests</c>, as <c>/tdd</c> does — outside any chat run: the person's click is no
    /// call the model made, so an open turn never lists it in <c>/replay</c> nor ties what the tests wrote to its
    /// <c>/undo-run</c>.
    /// </summary>
    internal static async Task<string> RunTestAsync(Execution.ToolRegistry tools, string filter, string? file, string? rootDir,
        CancellationToken ct)
    {
        var args = new Dictionary<string, string> { ["filter"] = filter };
        if (RunPath(file, rootDir) is { } path) args["path"] = path;
        using (tools.History.OutsideRun())
            return await tools.ExecuteAsync("run_tests", JsonSerializer.SerializeToElement(args), ct);
    }

    /// <summary>
    /// Where the test runs from: its file when the file names its runner (a C# file runs its project, a Python file
    /// itself), the workspace root otherwise — a JavaScript test's folder holds no <c>package.json</c> to find its
    /// runner by.
    /// </summary>
    internal static string? RunPath(string? file, string? rootDir) =>
        !string.IsNullOrEmpty(file) && Tools.RunTestsTool.RunnerOfPath(file) is not null
            && Tools.PathSanitizer.IsUnderRoot(Path.GetFullPath(file), rootDir)
            ? file
            : string.IsNullOrWhiteSpace(rootDir) ? null : rootDir;

    /// <summary>
    /// A refusal, shown in a notification: its sentence and the report's first line — never the whole raw report.
    /// </summary>
    private static ExplainAsk Refused(string why) =>
        new(string.Empty, string.Empty, string.Empty, string.Join("\n", why.Split('\n').Take(3)).TrimEnd());
}
