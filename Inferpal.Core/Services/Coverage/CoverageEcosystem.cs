using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Coverage;

/// <summary>A coverage measure: the report, and how the run went.</summary>
/// <param name="Report"><c>null</c> when no Cobertura report came out.</param>
/// <param name="Passed">The tests passed (a red suite still measures what it ran).</param>
internal sealed record CoverageRun(CoberturaReport? Report, bool Passed, string Output, bool TimedOut);

/// <summary>
/// What <c>/test coverage</c> needs from one ecosystem — .NET, pytest, a Node test runner: measure the suite or one test
/// file in Cobertura, run one test file, place a new test file where the runner collects it, and read a test's
/// assertions. Everything about the project is DETECTED (<see cref="CoverageEcosystems.DetectAsync"/>), never assumed.
/// </summary>
internal interface ICoverageEcosystem
{
    /// <summary>The tools, as the report names them ("pytest + coverage.py").</summary>
    string Name { get; }

    /// <summary>The test framework a new test file is written against, as the model is told it.</summary>
    string FrameworkOf(string testPath);

    /// <summary>The suite (<paramref name="testFile"/> null) or one test file, measured; the report under
    /// <paramref name="outDir"/>, a folder of its own outside the workspace.</summary>
    Task<CoverageRun> MeasureAsync(string? testFile, string outDir, CancellationToken ct);

    /// <summary>One test file run, without measuring.</summary>
    Task<(bool Passed, string Output, bool TimedOut)> RunAsync(string testFile, CancellationToken ct);

    /// <summary>Where a new test of <paramref name="sourceFile"/> goes: where the runner collects it, under a name no file
    /// has.</summary>
    string NewTestPath(string sourceFile);

    /// <summary>What the model is told of the new file beyond its path (a class name), or empty.</summary>
    string Hint(string testPath);

    /// <summary>Whether a test file asserts something.</summary>
    bool HasAssertion(string content);

    /// <summary>Whether a test file skips a test or expects one to fail.</summary>
    bool SkipsOrExpectsFailure(string content);
}

/// <summary>What the three ecosystems share: running a tool, and naming a new file.</summary>
internal static class CoverageTools
{
    /// <summary>One test file's run: a test that hangs fails at this deadline (the whole process tree killed), as it
    /// would at a longer one.</summary>
    internal static readonly TimeSpan FileRunDeadline = TimeSpan.FromSeconds(150);

    /// <summary>The whole suite, measured before and after: long suites exist; one that hangs is said, never waited for.</summary>
    internal static readonly TimeSpan SuiteDeadline = TimeSpan.FromMinutes(15);

    /// <summary>English and plain output, read the same everywhere; and no Python bytecode — pytest caches each test file it
    /// rewrites, and a file set aside would leave its <c>__pycache__</c> entry behind.</summary>
    private static readonly IReadOnlyDictionary<string, string> Environment = new Dictionary<string, string>
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en", ["VSLANG"] = "1033", ["PYTHONIOENCODING"] = "utf-8",
        ["PYTHONDONTWRITEBYTECODE"] = "1", ["NO_COLOR"] = "1", ["FORCE_COLOR"] = "0", ["CI"] = "1",
    };

    /// <summary>Runs <paramref name="fileName"/> with <paramref name="args"/> as a list (no shell) in
    /// <paramref name="cwd"/>; the output is stdout then stderr.</summary>
    internal static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string fileName, IEnumerable<string> args, string cwd, TimeSpan deadline, CancellationToken ct,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var psi = new ProcessStartInfo(fileName) { WorkingDirectory = cwd };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        foreach (var (key, value) in Environment) psi.Environment[key] = value;
        foreach (var (key, value) in extra ?? new Dictionary<string, string>()) psi.Environment[key] = value;
        try
        {
            var result = await ChildProcess.RunAsync(psi, deadline, ct);
            var output = result.Combined.Trim();
            return (result.TimedOut ? -1 : result.ExitCode,
                    result.TimedOut ? output + "\n" + ChildProcess.TimedOutMessage((int)deadline.TotalSeconds, null) : output,
                    result.TimedOut);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // The tool did not start: said with its name, never an empty red.
            return (-1, $"Could not start {fileName}: {Diagnostics.RootMessage(ex)}", false);
        }
    }

    /// <summary><paramref name="wanted"/>, or the same name followed by 2, 3… when a file already has it.</summary>
    internal static string Unique(string wanted, Func<string, bool> exists)
    {
        if (!exists(wanted)) return wanted;
        var dir  = Path.GetDirectoryName(wanted) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(wanted);
        var ext  = Path.GetExtension(wanted);
        // "foo.coverage.test" + ".ts", "FooCoverageTests" + ".cs": the number goes before the suffix that makes it a test
        // file — after it, the runner would not collect it.
        var split = name.LastIndexOf('.') is var dot and > 0 ? (name[..dot], name[dot..])
                  : name.EndsWith("Tests", StringComparison.Ordinal) ? (name[..^5], "Tests")
                  : (name, string.Empty);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{split.Item1}{n}{split.Item2}{ext}");
            if (!exists(candidate)) return candidate;
        }
    }

    internal static bool Matches(Regex regex, string content) => RegexBudget.IsMatch(regex, content);

    internal static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline, RegexBudget.Default);
}
