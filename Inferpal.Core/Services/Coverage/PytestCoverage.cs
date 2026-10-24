namespace Inferpal.Services.Coverage;

/// <summary>pytest, measured by coverage.py in the project's own interpreter.</summary>
/// <remarks>
/// ⚠ coverage.py writes its data file in the working folder: kept in the measure's own folder (<c>COVERAGE_FILE</c>),
/// and pytest's cache off — the workspace gets the new test files and nothing else.
/// ⚠ <paramref name="pytestCov"/>: a project whose <c>addopts</c> turns pytest-cov on (<c>--cov=…</c>) starts a second
/// coverage inside ours, which then records almost nothing — every new test "covers no new line" and the measure before
/// is wrong too. <c>--no-cov</c> switches it off on every run (it also stops it writing its reports in the workspace).
/// ⚠ <paramref name="xdist"/>: a project whose <c>addopts</c> spreads its tests over workers (<c>-n auto</c>) runs them in
/// processes our measure does not follow — <c>-n 0</c> keeps them in it.
/// ⚠ Parallel mode, then combined: a project that configures <c>parallel = true</c> leaves only suffixed data files,
/// and <c>coverage xml</c> alone finds no data. And the report is read whatever <c>coverage xml</c> answers: a project's
/// <c>fail_under</c> makes it exit 2 after writing it, and one test file is always under the project's threshold.
/// </remarks>
/// <param name="pytestCov">Whether the interpreter has pytest-cov.</param>
/// <param name="xdist">Whether the interpreter has pytest-xdist.</param>
internal sealed class PytestCoverage(string root, string folder, string python, bool pytestCov, bool xdist = false) : ICoverageEcosystem
{
    private static readonly System.Text.RegularExpressions.Regex Assertion =
        CoverageTools.Pattern(@"\bassert\b|pytest\.raises|self\.assert");
    private static readonly System.Text.RegularExpressions.Regex Skip =
        CoverageTools.Pattern(@"pytest\.mark\.skip|unittest\.skip|pytest\.skip\(|xfail");
    private static readonly System.Text.RegularExpressions.Regex NothingRan =
        CoverageTools.Pattern(@"\bno tests ran\b|collected 0 items");

    public string Name => "pytest + coverage.py";

    public string FrameworkOf(string testPath) => "pytest";

    /// <summary>The measure's source: the folder asked for, or the whole workspace.</summary>
    private string Source => folder.Length == 0 ? root : Path.Combine(root, folder);

    /// <summary>The measure's command line: coverage.py running pytest, in parallel mode.</summary>
    internal List<string> MeasureArgs(string? testFile) =>
        ["-m", "coverage", "run", "--parallel-mode", $"--source={Source}", "-m", .. PytestArgs(testFile)];

    /// <summary>pytest on the suite, or on one file: quiet, no cache, pytest-cov and xdist off when the interpreter has
    /// them.</summary>
    internal List<string> PytestArgs(string? testFile) =>
        ["pytest", "-q", "-p", "no:cacheprovider", .. pytestCov ? ["--no-cov"] : Array.Empty<string>(),
         .. xdist ? ["-n", "0"] : Array.Empty<string>(), .. testFile is null ? Array.Empty<string>() : [testFile]];

    public async Task<CoverageRun> MeasureAsync(string? testFile, string outDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var data = new Dictionary<string, string> { ["COVERAGE_FILE"] = Path.Combine(outDir, ".coverage") };
        var (code, output, timedOut) = await CoverageTools.RunAsync(python, MeasureArgs(testFile), root, Deadline(testFile), ct, data);
        var xml = Path.Combine(outDir, "coverage.xml");
        var (combineCode, combineOutput, _) = await CoverageTools.RunAsync(
            python, ["-m", "coverage", "combine"], root, TimeSpan.FromMinutes(2), ct, data);
        var (xmlCode, xmlOutput, _) = combineCode == 0
            ? await CoverageTools.RunAsync(python, ["-m", "coverage", "xml", "-o", xml], root, TimeSpan.FromMinutes(2), ct, data)
            : (combineCode, combineOutput, false);
        var report = CoberturaReport.Read(xml, root);
        return new CoverageRun(report, Passed(code, output), report is not null ? output : output + "\n" + xmlOutput, timedOut);
    }

    public async Task<(bool Passed, string Output, bool TimedOut)> RunAsync(string testFile, CancellationToken ct)
    {
        var (code, output, timedOut) = await CoverageTools.RunAsync(
            python, ["-m", .. PytestArgs(testFile)], root, CoverageTools.FileRunDeadline, ct);
        return (Passed(code, output), output, timedOut);
    }

    /// <summary>Passed means tests ran and none failed: a file pytest collects nothing from proves nothing.</summary>
    private static bool Passed(int code, string output) => code == 0 && !CoverageTools.Matches(NothingRan, output);

    private static TimeSpan Deadline(string? testFile) =>
        testFile is null ? CoverageTools.SuiteDeadline : CoverageTools.FileRunDeadline;

    public string NewTestPath(string sourceFile)
    {
        var usual = CodeActions.TestFilePathResolver.Resolve(sourceFile);   // tests/test_core.py, where pytest collects
        var dir   = Path.GetDirectoryName(usual) ?? root;
        var stem  = Path.GetFileNameWithoutExtension(sourceFile);
        return CoverageTools.Unique(Path.Combine(dir, $"test_{stem}_coverage.py"), File.Exists);
    }

    public string Hint(string testPath) => string.Empty;

    public bool HasAssertion(string content) => CoverageTools.Matches(Assertion, content);

    public bool SkipsOrExpectsFailure(string content) => CoverageTools.Matches(Skip, content);
}
