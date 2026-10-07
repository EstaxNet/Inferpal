using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Tools;

internal class RunTestsTool : ITool
{
    private const int DefaultTimeoutSeconds = 120;
    private const int MinTimeoutSeconds     = 1;
    private const int MaxTimeoutSeconds     = 1_800;
    private const int MaxRawChars           = 6000;

    private readonly Func<string?> _getRoot;

    /// <param name="getRoot">The workspace root: where tests run when no path is given, and what a
    /// relative path resolves against. ⚠ Never the process's working directory, which in Visual
    /// Studio is the out-of-process host's folder — "No test runner detected" on a solution full of
    /// tests, which the model reads as "there are no tests".</param>
    public RunTestsTool(Func<string?>? getRoot = null) => _getRoot = getRoot ?? (() => null);

    public string Name => "run_tests";

    public string Description =>
        "Runs the test suite and returns a summary (passed/failed/skipped) with error details for each failure. " +
        "Supports dotnet test (.sln/.csproj), pytest (Python, under the project's .venv or venv when there is one), npm test (Node.js), cargo test (Rust), and go test (Go). " +
        "The runner is auto-detected from project files; set 'runner' to force one. " +
        "Use 'filter' to run a specific test or class. " +
        "Typical workflow: fix code with apply_diff, then call run_tests to verify.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type        = "string",
                description = "Path to a project file (.sln/.csproj), directory, or test file. Optional, defaults to the workspace root."
            },
            filter = new
            {
                type        = "string",
                description = "Test name filter. dotnet: --filter expression (e.g. 'FullyQualifiedName~MyTest'). pytest: -k expression. npm: a test name pattern, passed to whichever of jest, vitest, mocha or node --test the test script runs. cargo: substring filter. go: -run regexp."
            },
            runner = new
            {
                type        = "string",
                description = "Force a runner: 'dotnet', 'pytest', 'npm' (also 'jest', 'vitest', 'mocha', 'node'), 'cargo', or 'go'. Default: 'auto' (detected from project files)."
            },
            timeout_seconds = new
            {
                type        = "integer",
                description = $"Max seconds to wait, {MinTimeoutSeconds}-{MaxTimeoutSeconds}. Default: {DefaultTimeoutSeconds}."
            }
        },
        required = Array.Empty<string>(),
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var root    = _getRoot();
        var rawPath = args.Trimmed("path");
        var path    = string.IsNullOrWhiteSpace(rawPath) ? null : PathSanitizer.Sanitize(rawPath, root);
        // ⚠ Confined like every path-taking tool: a test run builds and runs the project's code, and this tool asks no
        // approval (background /task runs offer it).
        if (path is not null) PathSanitizer.AssertUnderRoot(path, root);
        var filter  = args.Trimmed("filter");
        var forced  = args.Keyword("runner");
        // ⚠ Bounded, and SAID: 0 — "no limit" to many a model — cancelled the run at once, reported as "stopped at its
        // budget of 0 s", and a negative value threw. There is no unlimited run: 0 or less takes the maximum.
        var asked = args.Int("timeout_seconds", DefaultTimeoutSeconds);
        var (timeout, timeoutNotice) = asked <= 0
            ? (MaxTimeoutSeconds, (string?)$"{ClampedArgument.NotePrefix}'timeout_seconds' was {asked}; there is no unlimited run, so this one "
                                          + $"uses the maximum, {MaxTimeoutSeconds}.")
            : ClampedArgument.Read(args, "timeout_seconds", DefaultTimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds);

        // A path that names nothing is a mistyped path, never "no path": falling back to the root runs the whole suite,
        // read as the result for the file that was asked for.
        if (path is not null && !File.Exists(path) && !Directory.Exists(path))
            return $"{PathNotFound}: {path}. Check the path, or omit it to run the whole suite.";

        var workDir = ResolveWorkDir(path, root);
        // ⚠ A runner the model named and this tool does not know is NAMED, never answered "no test runner detected —
        // set 'runner' explicitly": that is the remedy it just applied, and it reads "this project has no tests".
        if (forced is not null and not "auto" && ForcedRunner(forced) is null)
            return $"Unknown runner '{forced}'. Use one of: auto, dotnet, pytest, npm, cargo, go — jest, vitest, mocha "
                 + "and node --test run through npm.";
        var runner  = (forced is null or "auto") ? DetectRunner(workDir, path, root) : ForcedRunner(forced)!;

        // ⚠ The budget is decorated HERE, after the parser, never inside the log it reads: the
        // parsers compose their verdict line from summaries, so a sentence buried in the raw text
        // is dropped, and a killed run reported the pass of the projects that had finished.
        var budget = new RunBudget(timeout);
        var report = runner switch
        {
            "dotnet" => await RunDotnetAsync(workDir, root, path, filter, budget, ct),
            "pytest" => await RunPytestAsync(workDir, root, path, filter, budget, ct),
            "npm"    => await RunNpmAsync(workDir, root, path, filter, budget, ct),
            "cargo"  => await RunCargoAsync(workDir, path, filter, budget, ct),
            "go"     => await RunGoAsync(workDir, path, filter, budget, ct),
            _        => NoRunnerDetected(workDir, root),
        };
        return ClampedArgument.Above(timeoutNotice, budget.Wrap(report));
    }

    // ── Runner implementations ─────────────────────────────────────────────────

    private static async Task<string> RunDotnetAsync(string workDir, string? root, string? path, string? filter, RunBudget budget, CancellationToken ct)
    {
        // `dotnet test` takes a project or a solution, never a source file — given one it answers MSB4025, "the project
        // file could not be loaded". A source file runs the project it belongs to, and the report says so.
        string? note = null;
        if (path is not null && File.Exists(path) && !IsDotnetProject(path))
        {
            var project = ProjectDirs(Path.GetDirectoryName(path) ?? workDir, root)
                .Select(d => FilesIn(d).FirstOrDefault(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(p => p is not null);
            note = project is null ? null : PathDoesNotNarrow("dotnet", path, project);
            path = project;   // none found: dotnet's own error names what is missing
        }

        var (output, exitCode) = await RunProcessAsync("dotnet", string.Empty, workDir, budget, ct, DotnetTestArguments(path, filter));
        return note + ParseDotnetOutput(output, exitCode);
    }

    /// <summary>The <c>dotnet test</c> command line, one element per argument.</summary>
    /// <remarks>⚠ A list, never a string the values are quoted into: a folder ending with a separator,
    /// <c>"src\Tests\"</c>, escapes its own closing quote under Windows' rules, swallows the switches after it, and
    /// dotnet answers MSB1009 "Project file does not exist" for a folder that exists.</remarks>
    internal static List<string> DotnetTestArguments(string? path, string? filter)
    {
        var args = new List<string> { "test" };
        if (!string.IsNullOrWhiteSpace(path)) args.Add(path);
        args.AddRange(["--verbosity", "normal", "--nologo"]);
        if (!string.IsNullOrWhiteSpace(filter)) args.AddRange(["--filter", filter]);
        return args;
    }

    private static readonly string[] DotnetProjectExtensions = [".csproj", ".fsproj", ".vbproj", ".proj"];

    private static bool IsDotnetProject(string path) =>
        SolutionFiles.IsSolution(path)
        || DotnetProjectExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>The files directly in <paramref name="dir"/>; none when it cannot be listed.</summary>
    private static IEnumerable<string> FilesIn(string dir)
    {
        try { return Directory.GetFiles(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("RunTestsTool.FilesIn", ex);
            return [];
        }
    }

    private static async Task<string> RunPytestAsync(string workDir, string? root, string? path, string? filter, RunBudget budget, CancellationToken ct)
    {
        var python = ResolvePython(workDir, root, OperatingSystem.IsWindows(), Shell.ShellLauncher.FindOnPath, File.Exists,
                                   Environment.GetEnvironmentVariable("VIRTUAL_ENV"));
        var (cwd, args) = PytestInvocation(workDir, root, path, filter, File.Exists);

        var (output, exitCode) = await RunProcessAsync(python, string.Empty, cwd, budget, ct, args);
        return ParsePytestOutput(output, exitCode, python);
    }

    /// <summary>
    /// The interpreter pytest runs under: the project's virtual environment (<c>.venv</c> or <c>venv</c>, from the
    /// run's folder up to the workspace root), then the activated one, then the PATH's.
    /// </summary>
    /// <remarks>
    /// ⚠ macOS and current Debian and Ubuntu have no "python", only python3 — and they refuse installs into the
    /// system Python (PEP 668), so pytest lives in the project's environment: run under the PATH's Python, the answer
    /// is "No module named pytest". A venv ABOVE the workspace root is not the project's: it is not looked at.
    /// </remarks>
    internal static string ResolvePython(string workDir, string? root, bool isWindows,
        Func<string, string?> onPath, Func<string, bool> exists, string? virtualEnv)
    {
        var inEnv = isWindows ? Path.Combine("Scripts", "python.exe") : Path.Combine("bin", "python");
        foreach (var dir in ProjectDirs(workDir, root))
            foreach (var name in (string[])[".venv", "venv"])
                if (Path.Combine(dir, name, inEnv) is var candidate && exists(candidate))
                    return candidate;

        if (!string.IsNullOrWhiteSpace(virtualEnv) && Path.Combine(virtualEnv, inEnv) is var active && exists(active))
            return active;
        if (isWindows) return "python";
        // Neither on the PATH: python3, so the start failure names the name these systems use.
        return onPath("python3") is null && onPath("python") is not null ? "python" : "python3";
    }

    /// <summary>The run's folder and its parents up to the workspace root; the folder alone when it is not under it.</summary>
    private static IEnumerable<string> ProjectDirs(string workDir, string? root)
    {
        yield return workDir;
        if (root is null || !IsUnder(workDir, root) || Path.GetRelativePath(root, workDir) == ".") yield break;
        var top = Path.TrimEndingDirectorySeparator(root);
        for (var dir = Path.GetDirectoryName(workDir); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            yield return dir;
            if (PathComparer.Default.Equals(Path.TrimEndingDirectorySeparator(dir), top)) yield break;
        }
    }

    /// <summary>Whether <paramref name="dir"/> is <paramref name="root"/> or below it.</summary>
    private static bool IsUnder(string dir, string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var rel = Path.GetRelativePath(root, dir);
        return !(rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(rel));
    }

    /// <summary>The nearest of the run's folder and its parents, up to the workspace root, that holds one of <paramref name="markers"/>.</summary>
    private static string? NearestWith(string workDir, string? root, Func<string, bool> exists, params string[] markers) =>
        ProjectDirs(workDir, root).FirstOrDefault(d => markers.Any(m => exists(Path.Combine(d, m))));

    private static readonly string[] PytestMarkers = ["pytest.ini", "pyproject.toml", "setup.cfg", "tox.ini", "setup.py"];

    /// <summary>Where pytest runs, and what it is given: the project's root as working folder, the path as target.</summary>
    /// <remarks>
    /// ⚠ <c>python -m pytest</c> puts its WORKING folder on sys.path. Started in the folder of the test file it is
    /// asked for, the project's own package does not import — "1 error during collection", read as a broken test —
    /// and every other test of that folder runs too. The root is the nearest folder with a pytest or packaging file,
    /// up to the workspace root, else the workspace root; the file or folder asked for is passed to pytest.
    /// </remarks>
    internal static (string Cwd, IReadOnlyList<string> Args) PytestInvocation(
        string workDir, string? root, string? path, string? filter, Func<string, bool> exists)
    {
        var cwd = NearestWith(workDir, root, exists, PytestMarkers) ?? (IsUnder(workDir, root) ? root! : workDir);
        var args = new List<string> { "-m", "pytest", "-v", "--tb=short", "-q" };
        if (path is not null) args.Add(path);
        if (!string.IsNullOrWhiteSpace(filter)) { args.Add("-k"); args.Add(filter); }
        return (cwd, args);
    }

    /// <summary>What run_tests answers for a path that names nothing: nothing ran.</summary>
    internal const string PathNotFound = "Error: 'path' does not exist — nothing ran";

    /// <summary>How the note of a filter Node did not apply starts (a warning, not a plain note); every other note this
    /// tool writes above its report starts with <see cref="ClampedArgument.NotePrefix"/>.</summary>
    internal const string FilterNotAppliedPrefix = "⚠ The filter '";

    /// <summary>
    /// The report under the notes this tool put above it: what a reader of the VERDICT line reads. A note qualifies
    /// the report, so it comes first — and the readers of the first line read a green run under a note as red, and
    /// a red one as no verdict at all.
    /// </summary>
    internal static string WithoutNotes(string report)
    {
        var t = report.TrimStart();
        while (t.StartsWith(ClampedArgument.NotePrefix, StringComparison.Ordinal) || t.StartsWith(FilterNotAppliedPrefix, StringComparison.Ordinal))
        {
            var end = t.IndexOf("\n\n", StringComparison.Ordinal);
            if (end < 0) return string.Empty;
            t = t[(end + 2)..].TrimStart();
        }
        return t;
    }

    /// <summary>
    /// The note above a report whose runner cannot narrow its run to <paramref name="path"/> — npm, cargo and go run a
    /// project's whole suite. <c>null</c> when the path IS that project.
    /// </summary>
    internal static string? PathDoesNotNarrow(string runner, string? path, string ranIn) =>
        path is null || PathComparer.Default.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(ranIn))
            ? null
            : $"{ClampedArgument.NotePrefix}{runner} cannot run only '{path}' — the whole test suite of {ranIn} ran. Use 'filter' to narrow it.\n\n";

    /// <summary>What the pytest runner answers when the interpreter it ran has no pytest: nothing ran.</summary>
    internal const string PytestNotInstalled = "⚠ pytest is not installed for the Python interpreter that ran";

    /// <summary>
    /// Whether <paramref name="output"/> is <c>python -m pytest</c> run by an interpreter that has no pytest — the
    /// interpreter's own line, "…python3: No module named pytest". Unquoted: a test's <c>ModuleNotFoundError</c> quotes
    /// the module, and is a real failure.
    /// </summary>
    internal static bool PytestModuleMissing(string output) =>
        output.Contains("No module named pytest", StringComparison.Ordinal);

    private static async Task<string> RunNpmAsync(string workDir, string? root, string? path, string? filter, RunBudget budget, CancellationToken ct)
    {
        if (ResolveNpm(OperatingSystem.IsWindows(), Shell.ShellLauncher.FindOnPath, File.Exists) is not { } npm)
            return NpmNotRunnable;

        // The package the path belongs to: its test script is the one that runs, and the one whose filter form counts.
        workDir = NearestWith(workDir, root, File.Exists, "package.json") ?? workDir;
        var args = new List<string>(npm.Prefix) { "test" };
        Dictionary<string, string>? env = null;
        string? note = null;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            // Unreadable, the script says nothing: the filter goes out in jest's form.
            var script = PackageJson.TestScript(workDir);
            // On Windows npm runs through the node.exe beside it — the node its test script will get.
            var takesFlags = !IsNodeTestRunner(script)
                || await NodeOptionsTakeTestFlagsAsync(OperatingSystem.IsWindows() ? npm.FileName : "node", ct);
            var plan = NpmFilter(script, filter, Environment.GetEnvironmentVariable("NODE_OPTIONS"), takesFlags);
            args.AddRange(plan.Args);
            if (plan.NodeOptions is not null) env = new() { ["NODE_OPTIONS"] = plan.NodeOptions };
            note = plan.Note;
        }
        var (output, exitCode) = await RunProcessAsync(npm.FileName, string.Empty, workDir, budget, ct, args, env);
        return note + PathDoesNotNarrow("npm", path, workDir) + ParseNpmOutput(output, exitCode, filter);
    }

    private static bool IsNodeTestRunner(string? testScript) =>
        testScript is not null && Regex.IsMatch(testScript, @"(?<!\S)--test(?!\S)", RegexOptions.None, RegexBudget.Default);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _nodeTakesTestFlags = new();

    /// <summary>Whether this Node accepts <c>--test-name-pattern</c> in <c>NODE_OPTIONS</c> — asked of the Node itself.</summary>
    /// <remarks>
    /// ⚠ Node 20, 21 and early 22 refuse it ("is not allowed in NODE_OPTIONS", exit 9): the whole run failed on the
    /// filter's account, and /tdd read that as a red suite. The boundary is a 22.x minor, so the Node is asked, once.
    /// </remarks>
    private static async Task<bool> NodeOptionsTakeTestFlagsAsync(string node, CancellationToken ct)
    {
        if (_nodeTakesTestFlags.TryGetValue(node, out var known)) return known;
        try
        {
            var psi = new ProcessStartInfo(node) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("");
            psi.Environment["NODE_OPTIONS"] = "--test-name-pattern=probe";
            var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(15), ct);
            return _nodeTakesTestFlags[node] = !run.TimedOut && run.ExitCode == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("RunTestsTool.NodeOptionsTakeTestFlags", ex);
            return false;
        }
    }

    /// <summary>Where a filter goes: arguments after <c>--</c>, the <c>NODE_OPTIONS</c> to run with, and what to say
    /// when it cannot go anywhere.</summary>
    internal readonly record struct NpmFilterPlan(IReadOnlyList<string> Args, string? NodeOptions, string? Note = null)
    {
        public void Deconstruct(out IReadOnlyList<string> args, out string? nodeOptions)
        {
            args        = Args;
            nodeOptions = NodeOptions;
        }
    }

    /// <summary>
    /// Where a test-name filter goes for the framework the <c>test</c> script runs: arguments after <c>--</c>, or
    /// the <c>NODE_OPTIONS</c> value to run with.
    /// </summary>
    /// <remarks>
    /// ⚠ The flag is the framework's, not npm's: jest's <c>--testNamePattern</c> makes <c>node --test</c> answer
    /// "bad option" and run nothing, which a repair loop reads as a red suite. And Node's own runner cannot take it
    /// as an argument at all: npm appends arguments to the END of the script, and after a positional file
    /// (<c>node --test test/a.test.js</c>) Node hands them to the test file — the filter is ignored, every test
    /// runs, without a word. <c>NODE_OPTIONS</c> reaches it whatever the script's shape; its quotes keep a filter
    /// with spaces in one piece. mocha reads options anywhere on its command line.
    /// </remarks>
    /// <param name="nodeOptionsTakeTestFlags">
    /// Whether the Node that runs the script accepts the flag in <c>NODE_OPTIONS</c>. When it does not, a script that
    /// names no test file takes the flag as an argument (after <c>--test</c>, still an option there); a script that
    /// names its files cannot take it at all, and the whole suite runs — said, never silent.
    /// </param>
    internal static NpmFilterPlan NpmFilter(string? testScript, string filter, string? inheritedNodeOptions,
                                            bool nodeOptionsTakeTestFlags = true)
    {
        if (IsNodeTestRunner(testScript))
        {
            if (nodeOptionsTakeTestFlags)
            {
                var quoted = "--test-name-pattern=\"" + filter.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                return new([], string.IsNullOrWhiteSpace(inheritedNodeOptions) ? quoted : inheritedNodeOptions + " " + quoted);
            }
            if (!NamesAnOperandAfterTest(testScript!))
                return new(["--", $"--test-name-pattern={filter}"], null);
            return new([], null,
                $"{FilterNotAppliedPrefix}{filter}' was NOT applied: this Node does not accept --test-name-pattern in NODE_OPTIONS, and " +
                "the test script names its test files, after which an argument goes to the tests instead of Node. The " +
                "whole suite ran; its result is below.\n\n");
        }
        var flag = testScript is not null && Regex.IsMatch(testScript, @"\bmocha\b", RegexOptions.None, RegexBudget.Default)
            ? $"--grep={filter}"
            : $"--testNamePattern={filter}";   // jest and vitest — and the default when the script says nothing
        return new(["--", flag], null);
    }

    /// <summary>Whether anything but options follows <c>--test</c> in the script — a file, a glob, another command.</summary>
    private static bool NamesAnOperandAfterTest(string testScript)
    {
        var tokens = testScript.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var at = Array.IndexOf(tokens, "--test");
        return at < 0 || tokens.Skip(at + 1).Any(t => !t.StartsWith('-'));
    }


    /// <summary>What the npm runner answers when npm cannot be launched without a shell.</summary>
    internal const string NpmNotRunnable =
        "npm could not be run without a shell: npm.cmd was not found on PATH with node.exe and " +
        "node_modules\\npm\\bin\\npm-cli.js beside it. Run the tests with run_command instead.";

    /// <summary>The program and leading arguments that run npm WITHOUT a shell; <c>null</c> when that is not possible.</summary>
    /// <remarks>
    /// ⚠ On Windows npm is a batch script (npm.cmd) and CreateProcess only resolves an executable: launched as "npm", the
    /// runner does not start there. And launched as npm.cmd it would go through cmd.exe, which interprets the filter —
    /// written by the model, in a tool that asks no approval — so "&amp;", "|" or "%" in it would become commands. npm's
    /// own CLI, run by the node.exe that sits beside npm.cmd in every Node install, takes its arguments as a list.
    /// </remarks>
    internal static (string FileName, string[] Prefix)? ResolveNpm(bool isWindows, Func<string, string?> onPath, Func<string, bool> exists) =>
        !isWindows ? ("npm", [])   // a script with a shebang: exec runs it, no shell in between
                   : Shell.NodeShim.Resolve("npm", isWindows, onPath, exists);

    /// <summary>npm's placeholder "test" script ran — the one <c>npm init</c> writes. Nothing to fix.</summary>
    internal const string NoTestScript =
        "⚠ The project's \"test\" script is npm's placeholder (\"no test specified\") — nothing ran. " +
        "That is not a failure to fix.";

    /// <summary>
    /// A verdict line for <c>npm test</c>, from the summary its runner prints — jest, vitest, mocha or node --test — then
    /// the raw output, which carries the failures.
    /// </summary>
    /// <remarks>
    /// ⚠ The raw output is never a verdict: it starts with "&gt; project@1.0.0 test", and /tdd reads green only from a
    /// verdict line — handed over raw, a passing Node suite gets its fix rounds. A green summary with a failing exit is not green: <c>npm test</c> can chain a linter or a
    /// coverage gate after the tests. And no summary is never green, the rule of every other parser here.
    /// </remarks>
    internal static string ParseNpmOutput(string raw, int exitCode, string? filter = null)
    {
        var rawTail = Truncate(raw.Trim(), MaxRawChars);
        if (raw.Contains("Error: no test specified", StringComparison.Ordinal))
            return NoTestScript + "\n\n" + rawTail;

        // A pattern the framework cannot compile: jest and mocha name it in their error, the pattern included.
        if (!string.IsNullOrEmpty(filter)
            && raw.IndexOf("Invalid regular expression: /" + filter + "/", StringComparison.Ordinal) is var at and >= 0)
        {
            var end = raw.IndexOf('\n', at);
            return FilterRejected + "\n" + raw[at..(end < 0 ? raw.Length : end)].Trim() + "\n\n" + rawTail;
        }

        if (NpmSummary(raw) is not { } s)
            return exitCode == 0 ? NothingProven + "\n\n" + rawTail : rawTail;

        var counts = $"Failed: {s.Failed}, Passed: {s.Passed}, Skipped: {s.Skipped}, Total: {s.Total}";
        // ⚠ "Tests: 0 total" is ALSO what a test file that failed to load looks like — the test imports a module that
        // does not exist yet, the red state of test-first work. Only the suite count tells it from a filter that matched
        // nothing, and read as "nothing ran", /tdd stopped on "nothing to fix" before writing that module.
        var head = s.Failed == 0 && s.Passed == 0 && s.SuitesFailed > 0
                     ? $"✗ FAILED — {s.SuitesFailed} test file(s) failed to run before any test did; the error is below"
                 : s.Failed == 0 && s.Passed == 0 ? NoTestMatchedFilter
                 : s.Failed > 0                   ? $"✗ FAILED — {counts}"
                 : s.SuitesFailed > 0             ? $"✗ FAILED — {counts}, and {s.SuitesFailed} test file(s) failed to run; the error is below"
                 : exitCode != 0                  ? $"✗ FAILED — npm test exited with code {exitCode} although its test summary passed ({counts})"
                 :                                  $"✓ PASSED — {counts}";
        return head + "\n\n" + rawTail;
    }

    private readonly record struct NpmCounts(int Failed, int Passed, int Skipped, int Total, int SuitesFailed = 0);

    private static NpmCounts? NpmSummary(string raw)
    {
        static int Num(Match m) => m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        static Match Last(string text, string pattern) =>
            Regex.Matches(text, pattern, RegexOptions.Multiline, RegexBudget.Default) is { Count: > 0 } all ? all[^1] : Match.Empty;

        // jest: "Tests:       1 failed, 2 passed, 3 total" — vitest: "      Tests  1 failed | 1 passed (2)"
        // ⚠ "\r?$": under Multiline "$" matches before "\n" only, so a CRLF report left every summary line unread.
        var tests = Last(raw, @"^\s*Tests:?[ \t]+([^\r\n]*\b(?:passed|failed|skipped|total)\b[^\r\n]*)\r?$");
        if (tests.Success)
        {
            var body    = tests.Groups[1].Value;
            var failed  = Num(Regex.Match(body, @"(\d+) failed", RegexOptions.None, RegexBudget.Default));
            var passed  = Num(Regex.Match(body, @"(\d+) passed", RegexOptions.None, RegexBudget.Default));
            var skipped = Num(Regex.Match(body, @"(\d+) (?:skipped|todo)", RegexOptions.None, RegexBudget.Default));
            var total   = Regex.Match(body, @"(\d+) total|\((\d+)\)", RegexOptions.None, RegexBudget.Default) is { Success: true } t
                ? int.Parse(t.Groups[1].Success ? t.Groups[1].Value : t.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
                : failed + passed + skipped;
            // jest: "Test Suites: 1 failed, 1 total" — vitest: " Test Files  1 failed (1)"
            var suites = Last(raw, @"^\s*Test (?:Suites:|Files)[ \t]+([^\r\n]*)\r?$");
            var suitesFailed = suites.Success ? Num(Regex.Match(suites.Groups[1].Value, @"(\d+) failed", RegexOptions.None, RegexBudget.Default)) : 0;
            return new(failed, passed, skipped, total, suitesFailed);
        }

        // mocha: "  5 passing (12ms)", "  1 failing", "  2 pending"
        var passing = Last(raw, @"^\s*(\d+) passing\b");
        var failing = Last(raw, @"^\s*(\d+) failing\b");
        if (passing.Success || failing.Success)
        {
            var pending = Num(Last(raw, @"^\s*(\d+) pending\b"));
            return new(Num(failing), Num(passing), pending, Num(failing) + Num(passing) + pending);
        }

        // node --test: "# pass 3" (tap reporter) or "ℹ pass 3" (spec reporter — the default from Node 23 on, even when
        // the output is not a terminal). Read only in its tap form, every green run on a current Node is "no summary".
        var pass = Last(raw, @"^(?:#|ℹ) pass (\d+)");
        var fail = Last(raw, @"^(?:#|ℹ) fail (\d+)");
        if (pass.Success || fail.Success)
        {
            var skip = Num(Last(raw, @"^(?:#|ℹ) skipped (\d+)"));
            var all  = Last(raw, @"^(?:#|ℹ) tests (\d+)");
            return new(Num(fail), Num(pass), skip, all.Success ? Num(all) : Num(fail) + Num(pass) + skip);
        }
        return null;
    }

    private static async Task<string> RunCargoAsync(string workDir, string? path, string? filter, RunBudget budget, CancellationToken ct)
    {
        // cargo searches up for Cargo.toml, but run from the crate/workspace root for predictability.
        var root = FindUp(workDir, "Cargo.toml") ?? workDir;
        List<string> args = ["test", "--quiet"];
        if (!string.IsNullOrWhiteSpace(filter)) args.Add(filter);

        var (output, exitCode) = await RunProcessAsync("cargo", string.Empty, root, budget, ct, args);
        return PathDoesNotNarrow("cargo", path, root) + ParseCargoOutput(output, exitCode);
    }

    private static async Task<string> RunGoAsync(string workDir, string? path, string? filter, RunBudget budget, CancellationToken ct)
    {
        var root = FindUp(workDir, "go.mod") ?? workDir;
        var (output, exitCode) = await RunProcessAsync("go", string.Empty, root, budget, ct, GoTestArguments(filter));
        return PathDoesNotNarrow("go", path, root) + ParseGoOutput(output, exitCode);
    }

    /// <remarks>
    /// ⚠ A filtered run asks for -v: without it go prints the same "ok <package>" line for a skipped test as for a
    /// passing one, and a /tdd round that skips the failing test reads as green. Only a filtered run — a whole suite
    /// in -v is every passing test, which pushes failures out of a bounded capture.
    /// </remarks>
    internal static List<string> GoTestArguments(string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? ["test", "./..."] : ["test", "./...", "-v", "-run", filter];

    // ── Output parsers ─────────────────────────────────────────────────────────

    /// <summary>
    /// What a runner that exits 0 without a readable summary is worth: <b>nothing proven</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately carries no <c>✓</c>. <see cref="Commands.TddCommandHandler.TestsPassed"/> reads
    /// a leading <c>✓</c> as green, so this line keeps the loop running instead of letting it
    /// announce a victory nobody measured — same tactic as the "no test matched the filter"
    /// message, and same rule: <b>never infer green from an exit code alone</b>.
    /// </para>
    /// <para>
    /// A runner whose summary format moves — which is exactly what vstest did between SDK 8 and 9 —
    /// would otherwise turn every red run green, in silence. The raw output still follows, which is
    /// what the model actually reads.
    /// </para>
    /// </remarks>
    internal const string NothingProven =
        "⚠ The runner exited 0 but no test summary could be parsed — nothing was proven. " +
        "Read the raw output below; do not treat this as a pass.";

    /// <summary>The filter matched no test — the other way a run proves nothing.</summary>
    /// <remarks>
    /// A constant because <c>TddCommandHandler.NothingRan</c> has to recognise it: a sentence
    /// re-typed at the reading end keeps matching right up to the day the writing end is reworded,
    /// and then silently stops.
    /// </remarks>
    internal const string NoTestMatchedFilter =
        "⚠ No test matched the filter — nothing ran. " +
        "The tests may have been renamed or removed; that is not a pass.";

    /// <summary>
    /// The runner ran and found no test to execute, without saying whether a filter is why (go's
    /// <c>[no tests to run]</c> and <c>[no test files]</c>, cargo and pytest with nothing collected).
    /// </summary>
    /// <remarks>
    /// ⚠ These runners SAY so, and each says it in a form a summary parser takes for a verdict:
    /// cargo writes <c>test result: ok. 0 passed; 0 failed</c> and go an <c>ok</c> line — read as
    /// green, <c>/tdd</c> declares victory on a run where nothing ran, including the round where the
    /// failing test was renamed away; pytest exits 5 with <c>2 deselected</c>, read as red, and the
    /// loop spends five rounds fixing code against a filter.
    /// </remarks>
    internal const string NoTestFound =
        "⚠ The runner found no test to run — nothing ran, so nothing was proven. " +
        "If a filter was given, it matched nothing; that is not a pass.";

    /// <summary>Every test the run selected was skipped (or ignored): none executed.</summary>
    /// <remarks>
    /// ⚠ The report of such a run has a total and no failure — <c>Total tests: 1, Skipped: 1</c> — which
    /// a summary parser reads as green. And skipping the failing test is exactly the dishonest way for
    /// a fix loop to go green: <c>/tdd</c> declared victory on the round that added <c>Skip = "…"</c>.
    /// </remarks>
    /// <summary>The runner could not parse the test filter: nothing ran. Followed by the runner's own reason.</summary>
    internal const string FilterRejected =
        "⚠ The runner rejected the test filter — nothing ran, so nothing was proven. Its reason:";

    /// <summary>pytest refused its own command line (exit 4): nothing ran. Followed by its <c>ERROR:</c> lines.</summary>
    internal const string PytestUsageError =
        "⚠ pytest refused its command line (usage error) — nothing ran, so nothing was proven. Its reason:";

    internal const string OnlySkipped =
        "⚠ Every test this run selected was skipped — none executed, so nothing was proven; " +
        "that is not a pass.";

    /// <summary>The run was killed at its budget — the fourth state, and the only one that can
    /// carry a <b>green</b> summary while being worthless.</summary>
    /// <remarks>
    /// <para>
    /// The parsers read a log, not the clock: on a solution whose first project finishes and whose
    /// second hangs, the killed run still carries <c>Passed! - Failed: 0, Passed: 16</c>, and that
    /// became the whole report — <c>✓ PASSED</c>, with the projects that never ran nowhere in it.
    /// The rule this file already states about exit codes ("never infer green from an exit code
    /// alone") holds exactly as much for a summary that describes a fraction of the run.
    /// </para>
    /// <para>
    /// A constant for the same reason as its two neighbours: <c>TddCommandHandler</c> has to
    /// recognise this state, and a sentence re-typed at the reading end keeps matching right up to
    /// the day the writing end is reworded.
    /// </para>
    /// </remarks>
    internal const string StoppedAtBudget =
        "⚠ The test run was stopped before it finished — nothing was proven.";

    /// <summary>The sentence the report opens with when the budget ran out.</summary>
    internal static string StoppedAtBudgetLine(int seconds) =>
        $"{StoppedAtBudget} It exceeded its {seconds}s budget and the runner was killed, tree " +
        "included; anything below describes only the part that ran. Raise 'timeout_seconds', or " +
        "narrow 'filter' to the tests you are working on.";

    /// <summary>The heading of a run whose code did not compile: red, and no test ran.</summary>
    internal const string BuildFailed = "✗ BUILD FAILED — the code did not compile, so no test ran. Compiler errors:";

    /// <summary>dotnet refused its own command line (MSB1xxx): nothing was built, nothing ran. Followed by its errors.</summary>
    internal const string DotnetCommandRejected =
        "⚠ dotnet refused its command line (an MSBuild switch error, not the code) — nothing ran, so nothing was "
        + "proven. Give 'path' as one existing .csproj or .sln file. Its reason:";

    /// <summary>An MSBuild command-line error (MSB1001–MSB1999): about the invocation, never about the code.</summary>
    private static bool IsCommandLineError(string error) =>
        Regex.IsMatch(error, @"\berror MSB1\d{3}:", RegexOptions.None, RegexBudget.Default);

    private const int MaxCompileErrorsListed = 20;

    /// <summary>
    /// The distinct errors of a build log (<c>File.cs(12,31): error CS1061: …</c>, <c>error MSB4019: …</c>),
    /// without MSBuild's node prefix and project suffix — each is printed twice, inline and in the final summary.
    /// </summary>
    internal static List<string> CompileErrors(string raw)
    {
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (Match m in Regex.Matches(raw,
                     @"^\s*(?:\d+>)?(?<body>\S.*?\berror [A-Z]{2,}\d+:.*?)(?:\s+\[[^\]\r\n]*\])?\s*$",
                     RegexOptions.Multiline, RegexBudget.Default))
        {
            var body = m.Groups["body"].Value.Trim();
            if (seen.Add(body)) errors.Add(body);
        }
        return errors;
    }

    internal static string ParseDotnetOutput(string raw, int exitCode)
    {
        var sb = new StringBuilder();

        // Aggregate summary across all test projects
        // Format: "Passed! - Failed:     0, Passed:     5, Skipped:     0, Total:     5"
        var summaryRx = new Regex(
            @"(?:Passed|Failed|Skipped)!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)",
            RegexOptions.Multiline | RegexOptions.Compiled, RegexBudget.Default);

        int totalFailed = 0, totalPassed = 0, totalSkipped = 0, totalTotal = 0;
        foreach (Match m in summaryRx.Matches(raw))
        {
            totalFailed  += int.Parse(m.Groups[1].Value);
            totalPassed  += int.Parse(m.Groups[2].Value);
            totalSkipped += int.Parse(m.Groups[3].Value);
            totalTotal   += int.Parse(m.Groups[4].Value);
        }

        // Modern vstest (SDK 9/10) prints a multi-line block instead — the single-line format above
        // never matches there, so every run fell through to "no summary line detected" (green) or
        // the raw dump (red):
        //   Test Run Successful.
        //   Total tests: 16
        //        Passed: 16
        //        Failed: 2        (only when non-zero)
        if (totalTotal == 0)
        {
            var blockRx = new Regex(
                @"^Total tests:\s*(\d+)\s*$(?:\r?\n^\s+Passed:\s*(\d+)\s*$)?(?:\r?\n^\s+Failed:\s*(\d+)\s*$)?(?:\r?\n^\s+Skipped:\s*(\d+)\s*$)?",
                RegexOptions.Multiline | RegexOptions.Compiled, RegexBudget.Default);
            foreach (Match m in blockRx.Matches(raw))
            {
                totalTotal   += int.Parse(m.Groups[1].Value);
                totalPassed  += m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                totalFailed  += m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
                totalSkipped += m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
            }
        }

        if (totalTotal > 0 && totalPassed + totalFailed == 0)
        {
            sb.AppendLine(OnlySkipped);
            sb.AppendLine($"Failed: 0, Passed: 0, Skipped: {totalSkipped}, Total: {totalTotal}");
        }
        else if (totalTotal > 0)
        {
            var status = totalFailed == 0 ? "✓ PASSED" : "✗ FAILED";
            sb.AppendLine($"{status} — Failed: {totalFailed}, Passed: {totalPassed}, Skipped: {totalSkipped}, Total: {totalTotal}");
        }
        // No test summary and compiler errors: the code did not build, so no test ran — and the errors ARE the
        // verdict. Red (a test written before its code does not compile: that is TDD's first step, not "nothing to
        // fix"), and never inferred from "Build FAILED.", which `dotnet test` also prints when a test merely fails.
        // MSBuild's MSB1xxx are COMMAND-LINE errors — which project, a file that does not exist, a bad switch: the code
        // was never built. Read as compiler errors, "MSB1011: specify which project" made /tdd edit sound code.
        else if (CompileErrors(raw) is { Count: > 0 } invocation && invocation.All(IsCommandLineError))
        {
            sb.AppendLine(DotnetCommandRejected);
            foreach (var e in invocation.Take(3))
                sb.AppendLine($"  {e}");
        }
        else if (CompileErrors(raw) is { Count: > 0 } errors)
        {
            sb.AppendLine(BuildFailed);
            foreach (var e in errors.Take(MaxCompileErrorsListed))
                sb.AppendLine($"  {e}");
            if (errors.Count > MaxCompileErrorsListed)
                sb.AppendLine($"  … +{errors.Count - MaxCompileErrorsListed} more error(s) not listed");
        }
        // A filter matching zero tests exits 0: read as a pass, an agent that renamed or deleted
        // the failing test makes `/tdd` declare victory on a run where nothing ran. The vstest
        // message is reliably English here because this tool forces the child's UI language.
        // No ✓/✗ prefix on purpose: callers' verdict parsing reads it as not-green and the loop
        // keeps working.
        // A filter vstest cannot parse also ends in "No test matches": its reason is on its own line, and it is not
        // "the tests were renamed".
        else if (raw.IndexOf("Incorrect format for TestCaseFilter", StringComparison.Ordinal) is var at and >= 0)
        {
            var end = raw.IndexOf('\n', at);
            sb.AppendLine(FilterRejected);
            sb.AppendLine(raw[at..(end < 0 ? raw.Length : end)].Trim());
        }
        else if (raw.Contains("No test matches the given testcase filter", StringComparison.Ordinal))
        {
            sb.AppendLine(NoTestMatchedFilter);
        }
        else if (exitCode == 0)
        {
            sb.AppendLine(NothingProven);
        }

        // Extract failed test blocks (name + error message, skip stack traces)
        var failures = ExtractDotnetFailures(raw);
        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failing tests:");
            foreach (var (name, details) in failures)
            {
                sb.AppendLine($"  ✗ {name}");
                foreach (var d in details)
                    sb.AppendLine($"    {d}");
                sb.AppendLine();
            }
        }

        var result = sb.ToString().Trim();

        // Fallback: nothing could be parsed, return raw truncated
        if (string.IsNullOrEmpty(result))
            return Truncate(raw.Trim(), MaxRawChars);

        return result;
    }

    private static List<(string Name, List<string> Details)> ExtractDotnetFailures(string raw)
    {
        var failures = new List<(string, List<string>)>();
        var lines    = raw.Split('\n');

        string? currentName = null;
        var currentDetails  = new List<string>();
        bool inError        = false;
        bool inStack        = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r').Trim();

            // Start of a failed test: "Failed SomeName [10 ms]" — the duration is part of the shape. ⚠ Under
            // --verbosity normal the SDK logs "Failed to load prune package data from PrunePackageData folder…" once
            // per project: read as a test named "to load prune package data", it was the whole report of a build that
            // did not compile.
            if (Regex.IsMatch(line, @"^Failed\s+\S.*\[[^\]]+\]$", RegexOptions.None, RegexBudget.Default))
            {
                if (currentName is not null)
                    failures.Add((currentName, currentDetails));

                var name = line["Failed ".Length..].Trim();
                // Remove trailing duration "[10 ms]"
                var bracket = name.LastIndexOf('[');
                if (bracket > 0) name = name[..bracket].Trim();

                currentName    = name;
                currentDetails = [];
                inError        = false;
                inStack        = false;
                continue;
            }

            if (currentName is null) continue;

            if (line == "Error Message:") { inError = true; inStack = false; continue; }
            if (line == "Stack Trace:")   { inStack = true; inError = false; continue; }

            if (inStack) continue; // skip stack frames entirely

            if (inError && !string.IsNullOrEmpty(line))
                currentDetails.Add(line);

            // Blank line after error details ends the block
            if (string.IsNullOrEmpty(line) && currentDetails.Count > 0)
            {
                failures.Add((currentName, currentDetails));
                currentName    = null;
                currentDetails = [];
                inError        = false;
            }
        }

        if (currentName is not null && currentDetails.Count > 0)
            failures.Add((currentName, currentDetails));

        return failures;
    }

    /// <summary>Failing test names listed at most — beyond it the list says how many it left out.</summary>
    /// <remarks>
    /// The list is a sample; the COUNT above it never is. A truncated list read as a complete one
    /// is how the `/tdd` loop concludes it has seen every failure — the same reasoning error the
    /// build-error path already paid for (SmartFixValidator).
    /// </remarks>
    private const int MaxFailingListed = 30;

    /// <summary>Go's <c>file.go:line:</c> diagnostic lines shown under a failing run; the rest are counted.</summary>
    internal const int MaxGoDetailLines = 20;

    /// <summary>Says what the "Failing tests:" list left out, or nothing when it left out nothing.</summary>
    private static void AppendMoreFailures(StringBuilder sb, int total, int listed)
    {
        if (total > listed)
            sb.AppendLine($"  … +{total - listed} more failing test(s) not listed");
    }

    /// <summary>A pytest summary with skipped tests and not one that executed.</summary>
    private static readonly Regex OnlySkippedRx = new(
        @"^(?!.*\b\d+ (?:passed|failed|errors?|xfailed|xpassed)\b).*\b\d+ skipped\b",
        RegexOptions.None, RegexBudget.Default);

    internal static string ParsePytestOutput(string raw, int exitCode, string? interpreter = null)
    {
        var sb = new StringBuilder();

        // Summary: "= 1 failed, 5 passed in 1.23s ="
        var summaryMatch = Regex.Match(raw, @"=+\s*(.+?in\s+[\d.]+\s*s)\s*=+", RegexOptions.Multiline, RegexBudget.Default);

        // "…python3: No module named pytest" (unquoted: `python -m`'s own line, not a test's ModuleNotFoundError).
        // Read raw, it is a red suite, and /tdd spends its rounds patching code against a runner that never ran.
        if (!summaryMatch.Success && PytestModuleMissing(raw))
            return $"{PytestNotInstalled}: {interpreter ?? "python"}. Install it in the project's virtual environment " +
                   "(.venv or venv — this runner uses it when it exists) or run the tests with run_command. Nothing ran." +
                   "\n\n" + Truncate(raw.Trim(), MaxRawChars);

        // Exit 5 is pytest's own "no tests collected" — a -k that deselects everything included.
        if (exitCode == 5)
            sb.AppendLine(Regex.IsMatch(raw, @"\b\d+ deselected\b", RegexOptions.None, RegexBudget.Default)
                ? NoTestMatchedFilter : NoTestFound);
        // "1 skipped, 2 deselected" exits 0: collected, then not one executed.
        else if (summaryMatch.Success && OnlySkippedRx.IsMatch(summaryMatch.Groups[1].Value))
            sb.AppendLine(OnlySkipped);

        // Exit 4 is pytest's usage error — a -k it cannot parse, an option it does not know. Its reason is on the
        // "ERROR:" lines at the top, which the summary ("no tests ran") drops: read as red, /tdd patched code
        // against a malformed filter.
        if (exitCode == 4)
        {
            var errors = raw.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("ERROR:", StringComparison.Ordinal)).Take(5).ToList();
            return PytestUsageError + "\n" + (errors.Count > 0 ? string.Join("\n", errors) : Truncate(raw.Trim(), MaxRawChars));
        }

        if (summaryMatch.Success)
            sb.AppendLine(summaryMatch.Groups[1].Value.Trim());
        else if (exitCode == 0)
            sb.AppendLine(NothingProven);

        // FAILED lines: "FAILED tests/test_x.py::test_name - AssertionError: ..."
        var allFailedLines = raw.Split('\n')
            .Where(l => l.TrimStart().StartsWith("FAILED ", StringComparison.Ordinal))
            .Select(l => l.Trim())
            .ToList();
        var failedLines = allFailedLines.Take(MaxFailingListed).ToList();

        if (failedLines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failing tests:");
            foreach (var l in failedLines)
                sb.AppendLine("  " + l);
            AppendMoreFailures(sb, allFailedLines.Count, failedLines.Count);
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? Truncate(raw.Trim(), MaxRawChars) : result;
    }

    // Cargo prints one summary line per test binary:
    //   "test result: ok. 5 passed; 0 failed; 1 ignored; 0 measured; 0 filtered out"
    // and "test <name> ... FAILED" for each failure. Aggregates across binaries.
    internal static string ParseCargoOutput(string raw, int exitCode)
    {
        var sb = new StringBuilder();

        var summaryRx = new Regex(
            @"test result:\s*(?:ok|FAILED)\.\s*(\d+) passed;\s*(\d+) failed;\s*(\d+) ignored(?:;\s*\d+ measured;\s*(\d+) filtered out)?",
            RegexOptions.Multiline | RegexOptions.Compiled, RegexBudget.Default);

        int passed = 0, failed = 0, ignored = 0, filteredOut = 0;
        bool any = false;
        foreach (Match m in summaryRx.Matches(raw))
        {
            any      = true;
            passed  += int.Parse(m.Groups[1].Value);
            failed  += int.Parse(m.Groups[2].Value);
            ignored += int.Parse(m.Groups[3].Value);
            if (m.Groups[4].Success) filteredOut += int.Parse(m.Groups[4].Value);
        }

        // "test result: ok. 0 passed; 0 failed" is cargo's report of a run in which nothing ran.
        if (any && passed + failed == 0)
            sb.AppendLine($"{(ignored > 0 ? OnlySkipped : filteredOut > 0 ? NoTestMatchedFilter : NoTestFound)}\n" +
                          $"Passed: 0, Failed: 0, Ignored: {ignored}, Filtered out: {filteredOut}");
        else if (any)
            sb.AppendLine($"{(failed == 0 ? "✓ PASSED" : "✗ FAILED")} — Failed: {failed}, Passed: {passed}, Ignored: {ignored}, Total: {passed + failed + ignored}");
        else if (exitCode == 0)
            sb.AppendLine(NothingProven);

        var allFailing = CargoFailingTests(raw);
        var failing    = allFailing.Take(MaxFailingListed).ToList();
        var messages   = CargoFailureMessages(raw);

        if (failing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failing tests:");
            foreach (var name in failing)
            {
                sb.AppendLine($"  ✗ {name}");
                foreach (var line in messages.GetValueOrDefault(name) ?? [])
                    sb.AppendLine($"    {line}");
            }
            AppendMoreFailures(sb, allFailing.Count, failing.Count);
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? Truncate(raw.Trim(), MaxRawChars) : result;
    }

    /// <summary>The failing tests of a cargo run, in the order cargo lists them.</summary>
    /// <remarks>⚠ Three shapes, all read: "test a::b ... FAILED" (verbose), "a::b --- FAILED" (what <c>--quiet</c> — the
    /// flag this runner passes — prints today), and the names cargo lists under its closing "failures:" header, the one
    /// form every version prints. Read on the first shape alone, a quiet run reported "1 failed" with no name.</remarks>
    internal static List<string> CargoFailingTests(string raw)
    {
        var names = new List<string>();
        var lines = raw.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.StartsWith("test ", StringComparison.Ordinal) && l.EndsWith("... FAILED", StringComparison.Ordinal))
                names.Add(l["test ".Length..^"... FAILED".Length].Trim());
            else if (l.EndsWith(" --- FAILED", StringComparison.Ordinal) && l[..^" --- FAILED".Length] is { Length: > 0 } quiet
                     && !quiet.Contains(' '))
                names.Add(quiet);
            else if (l == "failures:")
                // The closing list: indented names, no "----" block header, up to the blank line.
                for (var j = i + 1; j < lines.Length && lines[j].StartsWith("    ", StringComparison.Ordinal); j++)
                    names.Add(lines[j].Trim());
        }
        return names.Distinct().ToList();
    }

    /// <summary>Each failing test's own lines — where it panicked, what the assertion compared — from cargo's
    /// "---- name stdout ----" blocks.</summary>
    internal static Dictionary<string, List<string>> CargoFailureMessages(string raw)
    {
        var messages = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var line in raw.Replace("\r", "").Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("---- ", StringComparison.Ordinal) && l.EndsWith(" ----", StringComparison.Ordinal))
            {
                var name = l["---- ".Length..^" ----".Length];
                name = name.EndsWith(" stdout", StringComparison.Ordinal) ? name[..^" stdout".Length] : name;
                messages[name] = current = [];
                continue;
            }
            if (l == "failures:" || l.StartsWith("test result:", StringComparison.Ordinal)) { current = null; continue; }
            if (current is null || l.Length == 0 || l.StartsWith("note: run with `RUST_BACKTRACE", StringComparison.Ordinal))
                continue;
            if (current.Count < MaxMessageLines) current.Add(l);
        }
        return messages;
    }

    private const int MaxMessageLines = 6;

    // Go prints "--- FAIL: TestName (0.00s)" per failing test (even without -v) and per-package
    // "ok|FAIL  import/path  0.0s" lines. The exit code is the overall pass/fail signal.
    internal static string ParseGoOutput(string raw, int exitCode)
    {
        // A -run the test binary cannot parse: it says so on a "testing: invalid regexp" line and fails without a single
        // "--- FAIL:" — the report was "see output" without the output, read as red by /tdd.
        var rejected = raw.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("testing: invalid regexp", StringComparison.Ordinal)).Distinct().Take(3).ToList();
        if (rejected.Count > 0)
            return FilterRejected + "\n" + string.Join("\n", rejected);

        var sb = new StringBuilder();

        // ⚠ Count BEFORE capping. Read off the capped list, a suite with eighty failures reports
        // "30 failing test(s)": the loop fixes thirty, re-runs, finds fifty, and reads them as
        // regressions it just introduced. go is the one runner with no summary of its own, so this
        // number is the only one the model gets.
        var allFailing = Regex.Matches(raw, @"^\s*--- FAIL:\s+(\S+)", RegexOptions.Multiline, RegexBudget.Default)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        var failing = allFailing.Take(MaxFailingListed).ToList();

        if (exitCode == 0)
        {
            // go test prints no summary line: its exit code IS the verdict, and that is the one
            // runner where reading it is legitimate. What is not legitimate is reading it when
            // nothing ran: exit 0 without a single "ok <package>" line means every package was
            // "[no test files]" or matched nothing, and calling that a pass is the same silent
            // green as the three parsers above. And an "ok" line that ends in "[no tests to run]"
            // is go saying nothing ran in that package — a -run that matched nothing prints only those.
            var ranAPackage = Regex.IsMatch(raw, @"^ok\s+\S(?!.*\[no tests to run\]).*$", RegexOptions.Multiline, RegexBudget.Default);
            // Under -v (a filtered run), a skip prints "--- SKIP:" where a pass prints "--- PASS:"; the package line is
            // "ok" for both.
            var passes = Regex.Matches(raw, @"^\s*--- PASS:", RegexOptions.Multiline, RegexBudget.Default).Count;
            var skips  = Regex.Matches(raw, @"^\s*--- SKIP:", RegexOptions.Multiline, RegexBudget.Default).Count;
            sb.AppendLine(!ranAPackage              ? NoTestFound
                        : passes == 0 && skips > 0  ? OnlySkipped
                        : "✓ Tests passed (verdict from go's exit code — go test prints no summary).");
        }
        else
            sb.AppendLine($"✗ FAILED — {(allFailing.Count > 0 ? $"{allFailing.Count} failing test(s)" : "see output")}");

        if (failing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failing tests:");
            foreach (var name in failing)
                sb.AppendLine($"  ✗ {name}");
            AppendMoreFailures(sb, allFailing.Count, failing.Count);
        }

        // Surface the diagnostic lines Go prints under each failure (file:line: message).
        var allDetail = raw.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => Regex.IsMatch(l, @"\.go:\d+:", RegexOptions.None, RegexBudget.Default))
            .Select(l => l.Trim())
            .Distinct()
            .ToList();
        var detail = allDetail.Take(MaxGoDetailLines).ToList();
        if (exitCode != 0 && detail.Count > 0)
        {
            sb.AppendLine();
            foreach (var l in detail)
                sb.AppendLine("  " + l);
            // Counted: the lines left out belong to failures listed above, whose message would otherwise be missing
            // without a sign that it exists.
            if (allDetail.Count > detail.Count)
                sb.AppendLine($"  … {allDetail.Count - detail.Count} more diagnostic line(s) not shown");
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? Truncate(raw.Trim(), MaxRawChars) : result;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The budget of one run, and whether it ran out.
    /// </summary>
    /// <remarks>
    /// ⚠ It travels DOWN to <see cref="RunProcessAsync"/> so the fact can be said ONCE, at the
    /// funnel in <see cref="ExecuteAsync"/>. Said at the five runner sites instead, the sixth
    /// runner would be written without it — and the failure is silent, because the parsers happily
    /// compose a verdict out of a partial log.
    /// </remarks>
    private sealed class RunBudget(int seconds)
    {
        public int  Seconds { get; }      = seconds;
        public bool Expired { get; set; }

        public string Wrap(string report) =>
            Expired ? StoppedAtBudgetLine(Seconds) + "\n\n" + report : report;
    }

    private static async Task<(string Output, int ExitCode)> RunProcessAsync(
        string fileName, string arguments, string workDir, RunBudget budget, CancellationToken ct,
        IReadOnlyList<string>? argumentList = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                WorkingDirectory       = workDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };
            // A list, when given, is passed as such: each element one argument, no quoting to get wrong.
            foreach (var a in argumentList ?? []) psi.ArgumentList.Add(a);

            // The dotnet/vstest summary lines this tool parses ("Passed! - Failed: …",
            // "Failed X [10 ms]", "Error Message:") are localized by the SDK: on a French machine
            // every red run fell through to the raw-log fallback — a truncated MSBuild wall instead
            // of test names and assert messages. Forcing the child's UI language keeps the parser
            // input deterministic on every locale.
            psi.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"] = "en";
            psi.EnvironmentVariables["VSLANG"]                 = "1033";
            foreach (var kv in environment ?? new Dictionary<string, string>()) psi.EnvironmentVariables[kv.Key] = kv.Value;

            var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(budget.Seconds), ct);

            // A test run that overruns its budget keeps the partial log rather than throwing a
            // cancellation, which would abort the agent turn and lose every line already produced.
            // The runner is killed, tree included, or a stray `dotnet test` survives it.
            // ⚠ The FACT travels in the budget, not in this text: written into the log, the
            // sentence reached a parser that does not read prose, and a summary from the projects
            // that had finished became the verdict of a run that was killed.
            if (run.TimedOut)
            {
                budget.Expired = true;
                return (run.Combined, -1);
            }

            return (run.Combined, run.ExitCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)               { return ($"Failed to start '{fileName}': {ex.Message}", -1); }
    }

    /// <summary>
    /// "Nothing here to run" — plus, when it is true, "and there is a folder I could not look in".
    /// </summary>
    /// <remarks>
    /// ⚠ <see cref="DetectRunner"/> decides by WALKING, and a folder the walk cannot list leaves no
    /// trace in any count: the answer is self-consistent and reads as a fact about the repository
    /// when it is a fact about what this process may open. The remedy on its own makes it worse —
    /// "provide 'path'" points at a file inside the folder nobody can open. The gap is added as a
    /// CAUSE, never as a replacement: the remedy is still right in every other case.
    /// ⚠ The detector is only asked on this branch, so a workspace that resolves its runner pays
    /// nothing for it.
    /// </remarks>
    /// <summary>The runner a forced value names: one of the five, or a JavaScript framework this tool runs through npm
    /// (the filter's description names them) — <c>null</c> for anything else.</summary>
    internal static string? ForcedRunner(string forced) => forced switch
    {
        "dotnet" or "pytest" or "npm" or "cargo" or "go" => forced,
        "jest" or "vitest" or "mocha" or "node"          => "npm",
        _                                                => null,
    };

    private static string NoRunnerDetected(string workDir, string? root)
    {
        const string message = "No test runner detected. Provide 'path' to a project, or set "
                             + "'runner' explicitly (dotnet / pytest / npm / cargo / go).";

        // The sentence belongs to WalkGap: "cannot be listed" and "is a link, not followed" send
        // the reader to two different places, and that choice is made in one place only.
        return WorkspaceScan.FirstWalkGap(workDir, root) is { } gap
            ? $"{message}\n({gap.Sentence()})"
            : message;
    }

    private static string DetectRunner(string workDir, string? explicitPath, string? root)
    {
        if (explicitPath is not null)
        {
            // SolutionFiles recognises BOTH formats: a `path` pointing at a .slnx otherwise
            // picked the wrong runner.
            if (SolutionFiles.IsSolution(explicitPath) ||
                explicitPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return "dotnet";
            // A test file names its language, whatever folder it sits in: tests/test_x.py has no pytest marker
            // beside it, and "No test runner detected" reads as "there are no tests".
            if (explicitPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                return "pytest";
            if (explicitPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return "dotnet";
        }

        // WorkspaceScan: lazy (GetFiles materialized the whole tree before .Any()) and excluded
        // dirs skipped — a vendored .sln under node_modules must not flip the runner to dotnet.
        if (WorkspaceScan.EnumerateFiles(workDir, "*.sln").Any()  ||
            WorkspaceScan.EnumerateFiles(workDir, "*.slnx").Any() ||
            WorkspaceScan.EnumerateFiles(workDir, "*.csproj").Any())
            return "dotnet";

        if (File.Exists(Path.Combine(workDir, "pytest.ini"))     ||
            File.Exists(Path.Combine(workDir, "pyproject.toml")) ||
            File.Exists(Path.Combine(workDir, "conftest.py"))    ||
            WorkspaceScan.EnumerateFiles(workDir, "conftest.py").Any())
            return "pytest";

        if (File.Exists(Path.Combine(workDir, "package.json")))
            return "npm";

        if (FindUp(workDir, "Cargo.toml") is not null)
            return "cargo";

        if (FindUp(workDir, "go.mod") is not null)
            return "go";

        // A path below its project's root: the markers are in a parent folder, up to the workspace root.
        if (explicitPath is not null)
        {
            if (NearestWith(workDir, root, File.Exists, "pytest.ini", "pyproject.toml", "conftest.py") is not null)
                return "pytest";
            if (NearestWith(workDir, root, File.Exists, "package.json") is not null)
                return "npm";
        }

        return "unknown";
    }

    // Walks up from <paramref name="startDir"/> looking for <paramref name="fileName"/>; returns the
    // directory containing it, or <c>null</c>. Used to locate a Rust crate / Go module root from a
    // nested working directory.
    private static string? FindUp(string startDir, string fileName)
    {
        var dir = startDir;
        for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
        {
            try { if (File.Exists(Path.Combine(dir, fileName))) return dir; }
            catch { return null; }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>Where the runner starts: the path given, else the workspace root — the working
    /// directory only when no workspace root is known.</summary>
    internal static string ResolveWorkDir(string? path, string? root)
    {
        var fallback = string.IsNullOrEmpty(root) ? Directory.GetCurrentDirectory() : root;
        if (path is null)                    return fallback;
        if (Directory.Exists(path))          return path;
        if (File.Exists(path))               return Path.GetDirectoryName(path) ?? fallback;
        return fallback;
    }

    /// <summary>
    /// A runner's raw output within <paramref name="max"/> characters: its first lines (what ran) and, for the larger
    /// part, its END — the cut said where it is, on line ends.
    /// </summary>
    /// <remarks>
    /// ⚠ The head alone dropped what this output is shown for. Every runner prints its failures and its summary last
    /// — jest's failing suites and counts, pytest's FAILURES section, cargo's and go's failures, MSBuild's error recap
    /// — so a chatty run reached the model as progress and passing lines, under a verdict saying "the error is below".
    /// </remarks>
    internal static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;

        var headEnd = s.LastIndexOf('\n', max / 4);
        if (headEnd <= 0) headEnd = max / 4;
        var tailFrom  = s.Length - (max - headEnd);
        var lineStart = s.IndexOf('\n', tailFrom);
        var tailStart = lineStart >= 0 && lineStart + 1 < s.Length ? lineStart + 1 : tailFrom;
        if (char.IsLowSurrogate(s[tailStart])) tailStart++;   // never start on half a pair

        return s[..headEnd]
             + $"\n...[{tailStart - headEnd} characters cut here — the start and the end of the output are shown]\n"
             + s[tailStart..];
    }
}
