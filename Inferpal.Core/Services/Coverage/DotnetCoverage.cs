using System.Text.RegularExpressions;

namespace Inferpal.Services.Coverage;

/// <summary>
/// <c>dotnet test</c>, measured by the collector the test project references (coverlet's "XPlat Code Coverage") or, when
/// it references none, by the <c>dotnet-coverage</c> tool on the PATH.
/// </summary>
/// <param name="target">What the suite is run on: the solution or project <c>run_tests</c> would take.</param>
/// <param name="testProjects">The test projects of the workspace (Microsoft.NET.Test.Sdk or IsTestProject).</param>
/// <param name="dotnetCoverage">The <c>dotnet-coverage</c> executable; null when the collector is used.</param>
internal sealed class DotnetCoverage(string root, string target, IReadOnlyList<string> testProjects, string? dotnetCoverage)
    : ICoverageEcosystem
{
    private static readonly Regex Assertion = CoverageTools.Pattern(@"\bAssert\.|\.Should\(|\bShould\.|\bShouldly\b");
    private static readonly Regex Skip      = CoverageTools.Pattern(@"Skip\s*=|\[Ignore|\[Skip|Assert\.Inconclusive");
    // A declaration, at the start of its line: "the class under test" in a comment is no class.
    private static readonly Regex Classes   = CoverageTools.Pattern(
        @"^[ \t]*(?:(?:public|internal|private|protected|sealed|static|partial|abstract|file)[ \t]+)*class[ \t]+(\w+)");
    private static readonly Regex NoTest    = CoverageTools.Pattern(@"No test matches the given testcase filter|No test is available|Total tests: 0\b");

    public string Name => dotnetCoverage is null ? "dotnet test + coverlet" : "dotnet test + dotnet-coverage";

    public string FrameworkOf(string testPath) => Framework(ProjectOf(testPath));

    /// <summary>The test framework a project references, with its target framework: what the model writes against.</summary>
    internal static string Framework(string project)
    {
        var text = Read(project);
        var framework = text.Contains("\"xunit.v3", StringComparison.OrdinalIgnoreCase) ? "xUnit v3"
                      : text.Contains("\"xunit", StringComparison.OrdinalIgnoreCase)    ? "xUnit"
                      : text.Contains("\"NUnit", StringComparison.OrdinalIgnoreCase)    ? "NUnit"
                      : text.Contains("\"MSTest", StringComparison.OrdinalIgnoreCase)   ? "MSTest"
                      : "the project's test framework";
        var tfm = Regex.Match(text, @"<TargetFrameworks?>([^<]+)</TargetFrameworks?>", RegexOptions.None, RegexBudget.Default);
        return $"{framework} ({Path.GetFileNameWithoutExtension(project)}{(tfm.Success ? ", " + tfm.Groups[1].Value.Split(';')[0] : string.Empty)})";
    }

    private static string Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    /// <summary>Whether a project references coverlet's collector.</summary>
    internal static bool HasCollector(string project) =>
        Read(project).Contains("coverlet.collector", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a project is a test project.</summary>
    internal static bool IsTestProject(string projectText) =>
        projectText.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(projectText, @"<IsTestProject>\s*true\s*</IsTestProject>", RegexOptions.IgnoreCase, RegexBudget.Default);

    public async Task<CoverageRun> MeasureAsync(string? testFile, string outDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var on   = testFile is null ? target : ProjectOf(testFile);
        var test = new List<string> { "test", on, "-nologo" };
        if (testFile is not null) test.AddRange(["--filter", Filter(testFile)]);
        int code; string output; bool timedOut;
        if (dotnetCoverage is null)
        {
            test.AddRange(["--collect:XPlat Code Coverage", "--results-directory", outDir]);
            (code, output, timedOut) = await CoverageTools.RunAsync("dotnet", test, root, Deadline(testFile), ct);
        }
        else
        {
            var xml = Path.Combine(outDir, "coverage.cobertura.xml");
            (code, output, timedOut) = await CoverageTools.RunAsync(
                dotnetCoverage, ["collect", "dotnet " + string.Join(' ', test.Select(Quote)), "-f", "cobertura", "-o", xml],
                root, Deadline(testFile), ct);
        }
        return new CoverageRun(Merged(outDir), Passed(code, output), output, timedOut);
    }

    /// <summary>The reports in <paramref name="outDir"/> — dotnet-coverage's at its top, coverlet's one folder per test
    /// project below it — as one.</summary>
    private CoberturaReport? Merged(string outDir)
    {
        var reports = Directory.Exists(outDir)
            ? Directory.EnumerateDirectories(outDir).Prepend(outDir)
                       .SelectMany(d => Directory.EnumerateFiles(d, "*.cobertura.xml"))
                       .Select(f => CoberturaReport.Read(f, root)).OfType<CoberturaReport>().ToList()
            : [];
        return reports.Count == 0 ? null : CoberturaReport.Merge(reports);
    }

    public async Task<(bool Passed, string Output, bool TimedOut)> RunAsync(string testFile, CancellationToken ct)
    {
        var (code, output, timedOut) = await CoverageTools.RunAsync(
            "dotnet", ["test", ProjectOf(testFile), "-nologo", "--filter", Filter(testFile)], root, CoverageTools.FileRunDeadline, ct);
        return (Passed(code, output), output, timedOut);
    }

    private static bool Passed(int code, string output) => code == 0 && !CoverageTools.Matches(NoTest, output);

    private static TimeSpan Deadline(string? testFile) => testFile is null ? CoverageTools.SuiteDeadline : CoverageTools.FileRunDeadline;

    /// <summary>The classes a test file declares, as a filter: the new file's tests and nothing else.</summary>
    internal static string Filter(string testFile)
    {
        var names = Classes.Matches(Read(testFile)).Select(m => m.Groups[1].Value).Distinct().ToList();
        if (names.Count == 0) names.Add(Path.GetFileNameWithoutExtension(testFile));
        return string.Join('|', names.Select(n => "FullyQualifiedName~" + n));
    }

    private static string Quote(string arg) => arg.Contains(' ') || arg.Contains('|') ? "\"" + arg + "\"" : arg;

    /// <summary>The project a file belongs to: the nearest .csproj above it.</summary>
    internal static string ProjectOf(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.csproj").Order(StringComparer.Ordinal).FirstOrDefault()
                    is { } project)
                return project;
            dir = Path.GetDirectoryName(dir);
        }
        return file;
    }

    /// <summary>Beside the source's usual tests, in the test project that references its project.</summary>
    /// <remarks>⚠ No test project references it: the resolver falls back to the source's own folder, where the file would
    /// be compiled into the code under test and collected by nobody. The source itself is returned instead — an existing
    /// file, which the command refuses before asking the model anything.</remarks>
    public string NewTestPath(string sourceFile)
    {
        var usual = CodeActions.TestFilePathResolver.Resolve(sourceFile);   // FooTests.cs in the test project
        var dir   = Path.GetDirectoryName(usual) ?? root;
        var path  = CoverageTools.Unique(Path.Combine(dir, Path.GetFileNameWithoutExtension(sourceFile) + "CoverageTests.cs"), File.Exists);
        return testProjects.Contains(ProjectOf(path), PathComparer.Default) ? path : sourceFile;
    }

    public string Hint(string testPath) =>
        $"The test class must be named {Path.GetFileNameWithoutExtension(testPath)} (the file's name), in the test project's namespace.";

    public bool HasAssertion(string content) => CoverageTools.Matches(Assertion, content);

    public bool SkipsOrExpectsFailure(string content) => CoverageTools.Matches(Skip, content);
}
