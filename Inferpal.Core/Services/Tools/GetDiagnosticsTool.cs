using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class GetDiagnosticsTool : ITool
{
    internal const string ToolName = "get_diagnostics";
    public string Name => ToolName;

    private readonly Services.Editor.IEditorSurface? _editor;
    private readonly Func<string?> _getRoot;

    /// <param name="editor">When the editor's live language-service diagnostics report an error,
    /// they are returned instantly instead of building; null / no error falls back to the compile
    /// flow (VS today).</param>
    /// <param name="getRoot">The workspace root: where the project file is looked for, and what a
    /// relative path resolves against. ⚠ Never the process's working directory, which in Visual
    /// Studio is the out-of-process host's folder, not the project.</param>
    public GetDiagnosticsTool(Services.Editor.IEditorSurface? editor = null, Func<string?>? getRoot = null)
    {
        _editor  = editor;
        _getRoot = getRoot ?? (() => null);
    }

    public string Description =>
        "Returns current errors and warnings. When the editor's live diagnostics report an " +
        "error, returns those instead (instant, but only the files the editor has analyzed, " +
        "and nothing is compiled); otherwise compiles the .NET project or solution — .NET only: for another " +
        "language, call it without path for the editor's diagnostics, or run that language's checker with run_command. " +
        "If path is omitted, looks for the first .sln or .csproj in the workspace root. " +
        "Timeout: 90 seconds.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type        = "string",
                description = "Path to the .sln/.slnx or project file, or a folder holding one (optional)."
            }
        },
        required = Array.Empty<string>(),
    };

    private static readonly Regex _diagLine = new(
        @":\s*(error|warning)\s+\w+\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default);

    internal static bool OutputHasErrors(string output) =>
        !string.IsNullOrEmpty(output) && _diagLine.IsMatch(output);

    // Errors only — warnings don't warrant auto-fix iterations.
    // Exposed as internal so SmartFixValidator can reuse without duplicating the pattern.
    internal static readonly Regex ErrorLineRegex = new(
        @":\s*error\s+\w+\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default);

    internal static bool OutputHasBuildErrors(string output) =>
        !string.IsNullOrEmpty(output) && ErrorLineRegex.IsMatch(output);

    // The line shape vscode/src/editorBridge.ts writes: `rel(line,col): sev source code: message`.
    // ⚠ Not ErrorLineRegex: a source and a code such as `eslint no-unused-vars` are two words, and
    // `\w+` stops at the hyphen. A drift here fails safe — an unrecognised error only costs a build.
    private static readonly Regex _panelErrorLine = new(
        @"\(\d+,\d+\):\s*error\s", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default);

    internal static bool PanelReportsErrors(string panel) => _panelErrorLine.IsMatch(panel);

    /// <summary>
    /// A panel answer says what it is: without the line it reads as the build's answer — a list of
    /// errors taken for all of them, when unopened files were never analyzed and nothing compiled.
    /// </summary>
    private static string FromPanel(string panel) =>
        Strings.DiagFromEditor + "\n\n" + ErrorsFirst(panel.Split('\n').Select(l => l.TrimEnd('\r')));

    // The severity of a diagnostic line, in both shapes this tool lists — MSBuild's `file(l,c): error CS0103: …` and
    // the editor panel's `rel(l,c): error source code: …`. The leftmost match is the line's own severity.
    private static readonly Regex _severity = new(
        @":\s*(?<s>error|warning)\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>How many diagnostic lines an answer lists; the counts above them are always complete.</summary>
    internal const int MaxListed = 200;

    /// <summary>
    /// Diagnostic lines with the errors first, at most <paramref name="max"/> of them, and what was left out counted
    /// per severity. Other lines (an editor's own notes) follow, as they came.
    /// </summary>
    /// <remarks>
    /// ⚠ In the order a build or an editor reports them, errors sit among the warnings — and a project with hundreds of
    /// nullable or lint warnings is ordinary. Past the cap (the editor's, or the context's), the summary still said
    /// "2 errors" while the errors themselves were cut: the model fixed warnings, or concluded the errors were gone.
    /// </remarks>
    internal static string ErrorsFirst(IEnumerable<string> lines, int max = MaxListed)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var other = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var m = _severity.Match(line);
            if (!m.Success) other.Add(line);
            else if (m.Groups["s"].Value.Equals("error", StringComparison.OrdinalIgnoreCase)) errors.Add(line);
            else warnings.Add(line);
        }

        var shownErrors   = Math.Min(errors.Count, max);
        var shownWarnings = Math.Min(warnings.Count, max - shownErrors);
        var listed = errors.Take(shownErrors).Concat(warnings.Take(shownWarnings)).ToList();

        var left = new List<string>();
        if (errors.Count > shownErrors) left.Add($"{errors.Count - shownErrors} more error(s)");
        if (warnings.Count > shownWarnings) left.Add($"{warnings.Count - shownWarnings} more warning(s)");
        if (left.Count > 0) listed.Add($"… {string.Join(" and ", left)} not listed (errors are listed first).");

        listed.AddRange(other);
        return string.Join("\n", listed);
    }

    /// <summary>What an answer of this tool says about the build.</summary>
    internal enum BuildVerdict { Clean, Errors, NotBuilt }

    /// <summary>
    /// Clean only when the answer is one this tool writes after a build that ran —
    /// <see cref="Strings.DiagBuildOk"/>, or <see cref="Strings.DiagSummary"/> with zero errors. The absence
    /// of an error line proves nothing: a path not found, no project, a build that died without parseable
    /// diagnostics or a refused path carry none either.
    /// </summary>
    internal static BuildVerdict ReadVerdict(string output)
    {
        if (string.IsNullOrEmpty(output)) return BuildVerdict.NotBuilt;

        const string NameSentinel  = "\u0001";
        const int    CountSentinel = 918273645;
        var firstLine = output.Split('\n')[0].TrimEnd('\r');

        bool FirstLineIs(string shape)
        {
            var pattern = "^" + Regex.Escape(shape)
                .Replace(NameSentinel, ".+?")
                .Replace(CountSentinel.ToString(), @"\d+") + "$";
            try   { return Regex.IsMatch(firstLine, pattern, RegexOptions.None, RegexBudget.Default); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        // ⚠ Asked FIRST, before the error lines: a build killed at its budget carries whatever the
        // compiler had already printed, so it usually DOES contain error lines — and "Errors" sends
        // the /fix-build loop patching a fragment of a build that never finished.
        if (FirstLineIs(Strings.DiagBuildStopped(CountSentinel))) return BuildVerdict.NotBuilt;
        if (OutputHasBuildErrors(output))                         return BuildVerdict.Errors;

        foreach (var shape in new[] { Strings.DiagBuildOk(NameSentinel), Strings.DiagSummary(0, CountSentinel, NameSentinel) })
            if (FirstLineIs(shape)) return BuildVerdict.Clean;

        return BuildVerdict.NotBuilt;
    }

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rawPath = args.Str("path");

        // Editor fast path: live language-service diagnostics beat a 90 s build, but only
        // when the model didn't ask for a specific project (an explicit path means "build
        // THAT") and the editor reports an ERROR. A panel without one proves nothing about
        // unopened files — and warnings-only is its ordinary state (a lint rule, a nullable
        // warning in an open file), so treating "has lines" as "has problems" meant the build
        // never ran at all. Such a panel is kept for when there is nothing to compile.
        string? panel = null;
        if (_editor is not null && string.IsNullOrWhiteSpace(rawPath))
        {
            var live = await _editor.GetEditorDiagnosticsAsync(ct);
            if (!string.IsNullOrWhiteSpace(live))
            {
                panel = live!.Trim();
                if (PanelReportsErrors(panel)) return FromPanel(panel);
            }
        }

        var root = _getRoot();
        string? path = null;
        if (!string.IsNullOrWhiteSpace(rawPath))
        {
            path = PathSanitizer.Sanitize(rawPath, root);
            // ⚠ Confined like every path-taking tool: a build runs the project's MSBuild targets — code — and this tool
            // asks no approval (plan mode and background /task runs offer it as a read).
            PathSanitizer.AssertUnderRoot(path, root);
        }
        path ??= FindProjectFile(root);

        if (path is null)
        {
            // Nothing to compile (a TypeScript or Python workspace): the panel is all there is.
            if (panel is not null) return FromPanel(panel);

            // ⚠ "No .sln or .csproj found" is a CONCLUSION, and FindProjectFile reaches it by
            // walking: a folder the walk cannot list is absent from every count, so the answer is
            // self-consistent and reads as a fact about the repository. It is a fact about what
            // this process may open — and the remedy it names ("provide the path parameter") points
            // at a file inside that very folder. Said as a cause, next to the remedy, never instead.
            var gap = WorkspaceScan.FirstWalkGap(SearchStart(root), root);
            // ⚠ "Provide the path parameter" is the remedy only if a .NET project exists somewhere: in a JavaScript or
            // Python workspace there is nothing for this tool to build, and a model told to find a path ends its turn
            // or keeps looking. The remedy that works there is named too (model-facing, so English).
            return gap is null ? NoProject : $"{NoProject}\n({gap.Value.Sentence()})";
        }

        // A folder is what `dotnet build` resolves itself — the project it holds, or an error naming the ambiguity.
        if (!File.Exists(path) && !Directory.Exists(path))
            return Strings.ToolFileNotFound(path);

        // ⚠ `dotnet build` of anything but a solution or a project answers MSB4025 AT LINE 1 OF THAT FILE: given a
        // package.json or a pyproject.toml, the report reads "1 error(s) — package.json", an invitation to "fix" a file
        // that is fine.
        if (File.Exists(path) && !SolutionFiles.IsSolution(path) && !IsProjectFile(path))
            return $"{NotADotnetProject}: {Path.GetFileName(path)}. It compiles .sln, .slnx and project files only. " +
                   "For another language, call it without 'path' for the editor's live diagnostics, or run that " +
                   "language's own checker with run_command (tsc, mypy, cargo check, go vet…).";

        var psi = new ProcessStartInfo
        {
            FileName  = "dotnet",
            Arguments = $"build \"{path}\" --no-restore -v minimal",
            // ⚠ The SDK writes UTF-8. Left to the host's console code page, every French compiler error carried "┬á"
            // (the non-breaking space before its colon) and accented messages came back mangled — the text this tool,
            // /fix-build and "Fix with AI" hand the model.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding  = System.Text.Encoding.UTF8,
        };

        // 90 s, after which the build tree is killed: abandoned instead, MSBuild node processes
        // outlive the turn that started them.
        var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(BudgetSeconds), ct);
        return Interpret(run, Path.GetFileName(path), BudgetSeconds);
    }

    /// <summary>The answer when no .NET project is found: the user's remedy, then the model's — in a workspace of
    /// another language this tool has nothing to build.</summary>
    internal static string NoProject =>
        Strings.DiagNoProject + "\nget_diagnostics builds .NET projects only. For another language, run its own "
        + "checker with run_command (tsc, mypy, cargo check, go vet…) or its tests with run_tests.";

    /// <summary>What the tool answers for a path that is not a .NET solution or project: nothing was built.</summary>
    internal const string NotADotnetProject = "Error: get_diagnostics builds .NET solutions and projects, and this is neither";

    private static bool IsProjectFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".proj";

    /// <summary>The budget of one build, after which the tree is killed.</summary>
    private const int BudgetSeconds = 90;

    /// <summary>What one build answers — <b>including</b> when there was no build to speak of.</summary>
    /// <remarks>
    /// ⚠ A build KILLED at its budget is not a build that has N errors. The compiler prints as it
    /// goes, so the partial output usually does carry error lines, and summarising them reads as a
    /// complete verdict — <c>"1 error(s), 0 warning(s) — X.csproj"</c> on a build that never reached
    /// the end, which <c>/fix-build</c> then spends up to five model rounds on. Same rule in
    /// <see cref="CodeActions.SmartFixValidator"/> and <see cref="RunTestsTool"/>, the two other
    /// readers of a killed child.
    /// ⚠ And the exit code of a killed child is <c>-1</c>, a sentinel of
    /// <see cref="ChildProcess"/> — printed raw it reads as the compiler's own answer.
    /// </remarks>
    internal static string Interpret(ChildProcessResult run, string projectFile, int budgetSeconds)
    {
        var diagnostics = run.Combined
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => _diagLine.IsMatch(l))
            .Select(l => l.Trim())
            .Distinct()
            .ToList();

        string body;
        if (diagnostics.Count == 0)
        {
            body = run.TimedOut ? run.Stdout.Trim()
                 : run.Succeeded ? Strings.DiagBuildOk(projectFile)
                                 : Strings.DiagBuildFailed(run.ExitCode, run.Stdout.Trim());
        }
        else
        {
            var errors   = diagnostics.Count(d => _diagLine.Match(d).Groups[1].Value.Equals("error",   StringComparison.OrdinalIgnoreCase));
            var warnings = diagnostics.Count(d => _diagLine.Match(d).Groups[1].Value.Equals("warning", StringComparison.OrdinalIgnoreCase));

            var sb = new StringBuilder();
            sb.AppendLine(Strings.DiagSummary(errors, warnings, projectFile));
            sb.AppendLine();
            sb.AppendLine(ErrorsFirst(diagnostics));
            body = sb.ToString().Trim();
        }

        return run.TimedOut
            ? (Strings.DiagBuildStopped(budgetSeconds) + "\n\n" + body).Trim()
            : body;
    }

    /// <summary>The first solution or project under <paramref name="root"/> — the working
    /// directory only when no workspace root is known.</summary>
    /// <summary>Where the search for a project starts — the one reader, so the gap reported to the
    /// caller is the gap of the walk that actually ran.</summary>
    private static string SearchStart(string? root) =>
        string.IsNullOrEmpty(root) ? Directory.GetCurrentDirectory() : root;

    internal static string? FindProjectFile(string? root)
    {
        var start = SearchStart(root);
        foreach (var ext in new[] { "*.sln", "*.slnx", "*.csproj" })
        {
            // WorkspaceScan: lazy + excluded dirs skipped — a stray .csproj under node_modules
            // or bin/ must not become "the" project file.
            var found = WorkspaceScan.EnumerateFiles(start, ext).FirstOrDefault();
            if (found is not null) return found;
        }
        return null;
    }
}
