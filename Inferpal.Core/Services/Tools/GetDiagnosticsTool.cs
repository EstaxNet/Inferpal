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
    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Runs;

    private readonly Services.Editor.IEditorSurface? _editor;
    private readonly Func<string?> _getRoot;

    /// <param name="editor">When the editor's live language-service diagnostics report an error,
    /// they are returned instantly instead of building; null / no error falls back to the compile
    /// flow (VS today).</param>
    /// <param name="getRoot">The workspace root: where the project file is looked for, and what a
    /// relative path resolves against. ⚠ Never the process's working directory, which in Visual
    /// Studio is the out-of-process host's folder, not the project.</param>
    /// <param name="consent">Asked before a model's build executes the workspace's code; <c>null</c> builds without
    /// asking.</param>
    public GetDiagnosticsTool(Services.Editor.IEditorSurface? editor = null, Func<string?>? getRoot = null,
                              WorkspaceCodeConsent? consent = null)
    {
        _editor  = editor;
        _getRoot = getRoot ?? (() => null);
        _consent = consent;
    }

    private readonly WorkspaceCodeConsent? _consent;

    public string Description =>
        "Returns current errors and warnings. When the editor's live diagnostics report an " +
        "error, returns those instead (instant, but only the files the editor has analyzed, " +
        "and nothing is compiled); otherwise compiles the .NET project or solution — .NET only: for another " +
        "language, run that language's checker with run_command (where the editor reports live diagnostics, a call " +
        "without path returns them). " +
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

    // ⚠ The code is optional: an MSBuild <Error Text="…"/> without a Code prints `X.csproj(5,5): error : …`. Required, a
    // build that failed on one next to a warning read "0 error(s), 1 warning(s)" — Clean, while dotnet exited 1.
    private static readonly Regex _diagLine = new(
        @":\s*(error|warning)(?:\s+\w+)?\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default);

    internal static bool OutputHasErrors(string output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        // These answers quote dotnet's own error line as their reason: read as a finding, the step goes red over code that
        // was never compiled.
        if (StoppedBeforeTheCompiler(UnderTheNote(output).Split('\n')[0].TrimEnd('\r'))) return false;
        return _diagLine.IsMatch(output) || _panelLine.IsMatch(output);
    }

    private static bool StoppedBeforeTheCompiler(string firstLine) =>
        firstLine == CommandLineRefused || firstLine == NotRestored || firstLine == EnvironmentRefused;

    /// <summary>The answer under the note that names what a build made by choice did not cover.</summary>
    private static string UnderTheNote(string output)
    {
        if (!output.StartsWith(NotBuiltPrefix, StringComparison.Ordinal)) return output;
        var after = output.IndexOf("\n\n", StringComparison.Ordinal);
        return after < 0 ? string.Empty : output[(after + 2)..];
    }

    // A diagnostic line of the editor panel (`rel(l,c): error ts 2304: …`): its source stands between the severity and
    // the code, which the build's shape does not allow.
    private static readonly Regex _panelLine = new(
        @"\(\d+,\d+\):\s*(error|warning)\s", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default);

    // Errors only — warnings don't warrant auto-fix iterations.
    // Exposed as internal so SmartFixValidator can reuse without duplicating the pattern.
    internal static readonly Regex ErrorLineRegex = new(
        @":\s*error(?:\s+\w+)?\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default);

    /// <summary>
    /// The first line of an answer where dotnet refused its command line (MSB1001–MSB1999: no project in the folder, a
    /// switch it does not know) — about the invocation, never the code: nothing was compiled.
    /// </summary>
    internal const string CommandLineRefused =
        "⚠ dotnet refused its command line (an MSBuild switch error, not the code) — nothing was compiled. Give 'path' as "
        + "one existing .csproj, .sln or .slnx file. Its reason:";

    /// <summary>
    /// The first line of an answer where dotnet stopped on a project its restore does not match: this tool builds with
    /// <c>--no-restore</c>, and a fresh clone or a pulled target framework is not an error in the code. The project named
    /// was not compiled; another project of a solution may have been, hence no word about the others.
    /// </summary>
    internal const string NotRestored =
        "⚠ A project is not restored, or was restored for something else (its project.assets.json) — it was not compiled, "
        + "so this says nothing about its code. Run `dotnet restore <project or solution>`, then check again. Its reason:";

    /// <summary>
    /// Whether <paramref name="errorLine"/> is dotnet stopping on its assets file: absent (NETSDK1004), without the target
    /// framework (NETSDK1005) or the runtime (NETSDK1047), or naming a package gone since the restore (NETSDK1064).
    /// </summary>
    /// <remarks>The diagnostic's code, never the word anywhere in the line: a path or a message holding it is a real
    /// error, and the model would not be shown it.</remarks>
    internal static bool IsNotRestored(string errorLine) =>
        Regex.IsMatch(errorLine, @"\berror\s+NETSDK(?:1004|1005|1047|1064)\b", RegexOptions.None, RegexBudget.Default);

    /// <summary>What is wrong when dotnet refuses this machine's SDK (<see cref="IsEnvironmentRefusal"/>,
    /// <see cref="IsSdkNotFound"/>): written once, for every reader that says it.</summary>
    internal const string EnvironmentMismatch = "the installed .NET SDK or a workload does not match what this project asks for";

    /// <summary>
    /// The first line of an answer where dotnet stopped before compiling because this machine's .NET SDK or workloads do
    /// not match what the project asks for (<see cref="BuildStop.Environment"/>), the projects not restored listed with
    /// them. No remedy: the line dotnet writes follows as the reason.
    /// </summary>
    internal const string EnvironmentRefused =
        "⚠ dotnet stopped before compiling a project: " + EnvironmentMismatch + " — it was not compiled, so this says "
        + "nothing about its code. Its reason:";

    /// <summary>
    /// Whether <paramref name="errorLine"/> is dotnet refusing this machine's SDK: a target framework it does not support
    /// (NETSDK1045), a workload that is not installed (NETSDK1147).
    /// </summary>
    /// <remarks>By the diagnostic's code, as <see cref="IsNotRestored"/>. ⚠ Not MSB4019 (an imported project not found):
    /// the line cannot tell a missing SDK file from an import the project — or the agent — broke, and counted here the
    /// agent that broke one would hear "nothing about the code".</remarks>
    internal static bool IsEnvironmentRefusal(string errorLine) =>
        Regex.IsMatch(errorLine, @"\berror\s+NETSDK(?:1045|1147)\b", RegexOptions.None, RegexBudget.Default);

    /// <summary>
    /// Whether <paramref name="output"/> is the dotnet host refusing a <c>global.json</c> whose SDK is not installed: it
    /// prints no error code, on stderr, before MSBuild starts. Both the current host's sentence and the one before .NET 8.
    /// </summary>
    internal static bool IsSdkNotFound(string output) =>
        output.Contains("A compatible .NET SDK was not found", StringComparison.Ordinal)
        || output.Contains("A compatible installed .NET SDK for global.json version", StringComparison.Ordinal);

    /// <summary>Why dotnet stopped before the compiler, by its error lines — <see cref="BuildStop.None"/> when one of them is
    /// the code's.</summary>
    internal enum BuildStop { None, CommandLine, NotRestored, Environment }

    /// <summary>
    /// Why dotnet stopped before the compiler (<see cref="BuildStop"/>): the one reading the build check, the check after an
    /// edit (<c>SmartFixValidator</c>) and the test run (<see cref="RunTestsTool"/>) share, so none of them reads as a
    /// compilation error what another reads as nothing compiled.
    /// </summary>
    /// <remarks>Every line or none, by each line's code. ⚠ The environment admits the projects not restored beside it: a fresh
    /// clone mixes both under <c>--no-restore</c>, and a rule per class reads that build red.</remarks>
    internal static BuildStop StopOf(IReadOnlyCollection<string> errorLines) =>
        errorLines.Count == 0                                                ? BuildStop.None
        : errorLines.All(RunTestsTool.IsCommandLineError)                    ? BuildStop.CommandLine
        : errorLines.All(IsNotRestored)                                      ? BuildStop.NotRestored
        : errorLines.All(l => IsNotRestored(l) || IsEnvironmentRefusal(l))   ? BuildStop.Environment
        :                                                                      BuildStop.None;

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
        output = UnderTheNote(output);
        if (output.Length == 0) return BuildVerdict.NotBuilt;

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
        // And before the error lines too: a refused command line carries the MSB1xxx line that says why, a project not
        // restored its NETSDK line.
        if (StoppedBeforeTheCompiler(firstLine) || firstLine == WorkspaceCodeConsent.Refusal) return BuildVerdict.NotBuilt;
        // ⚠ The editor panel's errors are errors too: its answer is only ever served when it lists one, and with the
        // source between the severity and the code (`error ts 2304:`) it read as "not built" — no end-of-turn notice,
        // and a ✓ on the run of a turn that ended on a broken file.
        if (OutputHasBuildErrors(output) || PanelReportsErrors(output)) return BuildVerdict.Errors;

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
        // Never in a run on a copy: the editor's panel reports the person's files, the copy is built instead.
        if (_editor is not null && string.IsNullOrWhiteSpace(rawPath) && Execution.RunRoot.Copy is null)
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
            // ⚠ Confined like every path-taking tool: a build runs the project's MSBuild targets — code — asked once per
            // workspace (WorkspaceCodeConsent); plan mode leaves it out for that reason (ToolCeilings).
            PathSanitizer.AssertUnderRoot(path, root);
        }
        var chosen = path is null;   // built by choice, not as asked: what it leaves out is named
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
            return ToolFailure.Said(Strings.ToolFileNotFound(path));

        // ⚠ `dotnet build` of anything but a solution or a project answers MSB4025 AT LINE 1 OF THAT FILE: given a
        // package.json or a pyproject.toml, the report reads "1 error(s) — package.json", an invitation to "fix" a file
        // that is fine.
        if (File.Exists(path) && !SolutionFiles.IsSolution(path) && !IsProjectFile(path))
            return $"{NotADotnetProject}: {Path.GetFileName(path)}. It compiles .sln, .slnx and project files only. " +
                   "For another language, run that language's own checker with run_command (tsc, mypy, cargo check, " +
                   "go vet…); where the editor reports live diagnostics, a call without 'path' returns them.";

        // A build runs the project's own targets: asked first, when a model asks for it.
        if (_consent is not null && await _consent.RefusalAsync(Name, ct) is { } refused) return refused;

        // ⚠ Arguments as a list: quoted into a string, a folder ending with a separator escapes its own closing quote
        // under Windows and dotnet answers MSB1009 "Project file does not exist" for a folder that exists.
        var psi = new ProcessStartInfo
        {
            FileName  = "dotnet",
            ArgumentList = { "build", path, "--no-restore", "-v", "minimal" },
            // ⚠ The SDK writes UTF-8. Left to the host's console code page, every French compiler error carried "┬á"
            // (the non-breaking space before its colon) and accented messages came back mangled — the text this tool,
            // /fix-build and "Fix with AI" hand the model.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding  = System.Text.Encoding.UTF8,
        };
        // In English, as for run_tests: NuGet Audit's lines are read by their words (VulnerablePackages).
        psi.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.EnvironmentVariables["VSLANG"]                 = "1033";

        // 90 s, after which the build tree is killed: abandoned instead, MSBuild node processes
        // outlive the turn that started them.
        var run    = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(BudgetSeconds), ct);
        var report = Interpret(run, Path.GetFileName(path), BudgetSeconds);
        return chosen && NotBuiltNote(root, path) is { } note ? note + "\n\n" + report : report;
    }

    /// <summary>How the note above a build made by choice starts — <see cref="ReadVerdict"/> reads under it.</summary>
    internal const string NotBuiltPrefix = "(This build covered ";

    /// <summary>
    /// The solutions — or, with none, the projects — of the workspace that a build of <paramref name="built"/> chosen by
    /// this tool does not cover; <c>null</c> when there are none.
    /// </summary>
    /// <remarks>
    /// ⚠ "✓ Build successful (Lib.csproj)" reads as "the code compiles": with a test project beside it — the ordinary
    /// layout of a folder opened in VS Code — the edited test project was not built, the verdict was Clean, and no
    /// end-of-turn notice fired. A solution builds its projects, so with one only the other SOLUTIONS are named; a
    /// project builds what it references, said in the note.
    /// </remarks>
    internal static string? NotBuiltNote(string? root, string built)
    {
        var start    = SearchStart(root);
        var solution = SolutionFiles.IsSolution(built);
        var kind     = solution
            ? WorkspaceScan.EnumerateFiles(start, "*").Where(SolutionFiles.IsSolution)
            : WorkspaceScan.EnumerateFiles(start, "*.csproj");
        var others = WorkspaceScan.ShallowestFirst(kind.Where(f => !PathComparer.Default.Equals(f, built)))
                                  .Select(f => Path.GetRelativePath(start, f).Replace('\\', '/'))
                                  .ToList();
        if (others.Count == 0) return null;

        var named = string.Join(", ", others.Take(5)) + (others.Count > 5 ? $" and {others.Count - 5} more" : "");
        return NotBuiltPrefix + Path.GetFileName(built) + (solution ? "" : " and what it references")
             + $". Not built: {named} — pass 'path' to build one.)";
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

    /// <summary>The budget of one build, after which the tree is killed — named by the command deadline's settings hint.</summary>
    internal const int BudgetSeconds = 90;

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
                 // The host refuses a global.json before MSBuild starts — on stderr, which the failure below does not show.
                 : IsSdkNotFound(run.Stderr) ? EnvironmentRefused + "\n" + run.Stderr.Trim()
                 : Strings.DiagBuildFailed(run.ExitCode, run.Stdout.Trim());
        }
        else
        {
            var errorLines = diagnostics.Where(d => _diagLine.Match(d).Groups[1].Value.Equals("error", StringComparison.OrdinalIgnoreCase)).ToList();
            var warnings   = diagnostics.Count(d => _diagLine.Match(d).Groups[1].Value.Equals("warning", StringComparison.OrdinalIgnoreCase));

            // ⚠ Not an error in the code: counted as one, "1 error(s)" turns an end-of-turn notice and the result bar red,
            // and the model repairs code that was never compiled.
            var stop = StopOf(errorLines);
            if (stop == BuildStop.CommandLine)
                body = CommandLineRefused + "\n" + string.Join("\n", errorLines);
            else if (stop == BuildStop.NotRestored)
                body = NotRestored + "\n" + ErrorsFirst(diagnostics);
            else if (stop == BuildStop.Environment)
                body = EnvironmentRefused + "\n" + ErrorsFirst(diagnostics);
            else if (errorLines.Count == 0 && !run.Succeeded && !run.TimedOut)
                // ⚠ dotnet failed and no error line is readable: "0 error(s)" read Clean. Its output is the report.
                body = Strings.DiagBuildFailed(run.ExitCode, run.Stdout.Trim());
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine(Strings.DiagSummary(errorLines.Count, warnings, projectFile));
                sb.AppendLine();
                sb.AppendLine(ErrorsFirst(diagnostics));
                body = sb.ToString().Trim();
            }
        }

        // ⚠ BELOW the report: its first line is the verdict (ReadVerdict, StoppedBeforeTheCompiler) — a section above it
        // read a green build "not built" and a restore stop as errors.
        if (VulnerablePackages(run.Combined) is { } vulnerable) body = body + "\n\n" + vulnerable;
        // Not under a restore stop: its own remedy (restore first) is the one to follow.
        if (!body.StartsWith(NotRestored, StringComparison.Ordinal) && SettingDecides(run.Combined) is { } setting) body = body + "\n\n" + setting;
        return run.TimedOut
            ? (Strings.DiagBuildStopped(budgetSeconds) + "\n\n" + body).Trim()
            : body;
    }

    // Errors a build SETTING decides, not the code: the language version (CS8370, CS8630, CS1617, CS8652), the output type
    // (CS5001), the target framework (NETSDK1045, NU1202), a nullability WARNING turned into an error — named one by one: the
    // CS86xx range also holds ordinary syntax errors (CS8641).
    private static readonly Regex SettingError = new(
        @"\berror (?<code>CS8370|CS8630|CS1617|CS8652|CS5001|NETSDK1045|NU1202|" +
        @"CS86(?:0[0-4]|0[7-9]|1\d|2[0-5]|29|3[1-4]|4[3-5]|55|67|70)|CS8714|CS87(?:6[2-9]|7[0-7])|CS8819|CS882[45]|CS8847)\b",
        RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>
    /// The pointer to <c>msbuild_property</c> under an error a build setting decides; <c>null</c> under any other.
    /// </summary>
    /// <remarks>⚠ The message names the symptom and the project file does not set the value: the model, left to it, adds a
    /// setting to the project or rewrites the code (measured, pari 99). Below the report, whose first line is the verdict.</remarks>
    internal static string? SettingDecides(string output)
    {
        var codes = SettingError.Matches(output).Select(m => m.Groups["code"].Value).Distinct().Take(4).ToList();
        if (codes.Count == 0) return null;
        var properties = codes.Select(c => c switch
        {
            "CS8370" or "CS8630" or "CS1617" or "CS8652" => "LangVersion",
            "CS5001" => "OutputType",
            "NETSDK1045" or "NU1202" => "TargetFramework and TargetFrameworks",
            _ => "TreatWarningsAsErrors and WarningsAsErrors",
        }).Distinct();
        return $"A build setting decides {string.Join(", ", codes)}, not the code: before changing the project or the code, call " +
               $"msbuild_property (when it is offered) for {string.Join("; ", properties)} — it names the file that sets the " +
               "value (a Directory.Build.props, an import, or a NuGet package outside the repository).";
    }

    // NuGet Audit's warnings, as `dotnet build` writes them in English (the build runs with DOTNET_CLI_UI_LANGUAGE=en);
    // `error NU1903` under WarningsAsErrors is the same line, and is a real build failure:
    // `X.csproj : warning NU1903: Package 'Newtonsoft.Json' 12.0.1 has a known high severity vulnerability, https://…`.
    private static readonly Regex AuditLine = new(
        @"\b(?<code>NU190[0-4])\b(?::\s*Package '(?<package>[^']+)' (?<version>\S+)[^,\n]*,\s*(?<url>https?://\S+))?",
        RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>
    /// The packages NuGet Audit flags in a build's output (<c>NU1901</c> to <c>NU1904</c>, low to critical), with the way to
    /// fix them; <c>null</c> when it flags none.
    /// </summary>
    /// <remarks>Listed among the warnings, they read as one more warning to leave: grouped above the report, with the command
    /// that fixes them. <c>NU1900</c>: the audit could not reach its source.</remarks>
    internal static string? VulnerablePackages(string output)
    {
        // One line per package: its worst severity and every advisory (NuGet writes one warning per advisory).
        var found = new SortedDictionary<string, (char Worst, SortedSet<string> Advisories)>(StringComparer.Ordinal);
        var unreachable = false;
        foreach (Match m in AuditLine.Matches(output))
        {
            var code = m.Groups["code"].Value;
            if (code == "NU1900") { unreachable = true; continue; }
            var key = m.Groups["package"].Success ? $"{m.Groups["package"].Value} {m.Groups["version"].Value}" : code;
            if (!found.TryGetValue(key, out var entry)) found[key] = entry = (code[^1], new SortedSet<string>(StringComparer.Ordinal));
            else if (code[^1] > entry.Worst) found[key] = entry = (code[^1], entry.Advisories);
            if (m.Groups["url"].Success) entry.Advisories.Add(m.Groups["url"].Value.TrimEnd('.', ']'));
        }
        const string Unreachable = "NuGet Audit could not reach the vulnerability data of a package source (NU1900): " +
                                   "packages from it were not checked.";
        if (found.Count == 0) return unreachable ? Unreachable : null;
        var lines = found.Select(f =>
            $"- {f.Key} — {f.Value.Worst switch { '1' => "low", '2' => "moderate", '3' => "high", _ => "critical" }}" +
            (f.Value.Advisories.Count > 0 ? " — " + string.Join(", ", f.Value.Advisories) : string.Empty));
        return "Vulnerable packages (NuGet Audit):\n" + string.Join("\n", lines) +
               "\nFix: `dotnet package update --vulnerable` raises each one to the lowest version without a known " +
               "vulnerability (a .NET 10 SDK or later; a global.json that pins an older SDK runs that one, and the command " +
               "does not exist there) — otherwise set the patched version the advisory names. Then build and run the tests." +
               (unreachable ? "\n" + Unreachable : string.Empty);
    }

    /// <summary>Where the search for a project starts — the one reader, so the gap reported to the
    /// caller is the gap of the walk that actually ran.</summary>
    /// <remarks>⚠ Never the working directory: in Visual Studio it is the host's own folder, and what is found there is built
    /// — MSBuild targets are code — with no approval card.</remarks>
    private static string SearchStart(string? root) =>
        string.IsNullOrEmpty(root) ? throw new NoWorkspaceRootException() : root;

    /// <summary>The solution or project under <paramref name="root"/> that is built — refused when no workspace root is
    /// known: the shallowest, then the first in ordinal order (<see cref="WorkspaceScan.ShallowestFirst"/>).</summary>
    /// <remarks>⚠ Both solution formats are ONE kind, compared together: searched one extension after the other, a
    /// <c>.sln</c> buried in <c>tools/</c> won over the <c>.slnx</c> at the root — the build of a side project, read as
    /// the workspace's verdict.</remarks>
    internal static string? FindProjectFile(string? root)
    {
        var start = SearchStart(root);
        // WorkspaceScan: lazy + excluded dirs skipped — a stray .csproj under node_modules
        // or bin/ must not become "the" project file.
        return WorkspaceScan.ShallowestFirst(WorkspaceScan.EnumerateFiles(start, "*").Where(SolutionFiles.IsSolution)).FirstOrDefault()
            ?? WorkspaceScan.ShallowestFirst(WorkspaceScan.EnumerateFiles(start, "*.csproj")).FirstOrDefault();
    }
}
