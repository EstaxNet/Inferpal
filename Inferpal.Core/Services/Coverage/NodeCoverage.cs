using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Coverage;

/// <summary>The Node test runners <c>/test coverage</c> measures: vitest (its own coverage), jest (its built-in one),
/// <c>node --test</c> (c8).</summary>
internal enum NodeRunner { Vitest, Jest, NodeTest }

/// <summary>
/// A Node project's tests, measured by the coverage its runner has, each tool run as <c>node &lt;its script&gt;</c>:
/// arguments as a list, never through <c>npx</c> or <c>cmd.exe</c>.
/// </summary>
/// <param name="script">The runner's script in <c>node_modules</c> (vitest, jest, or c8 for <c>node --test</c>).</param>
/// <param name="provider">vitest's coverage provider (<c>v8</c> or <c>istanbul</c>); unused otherwise.</param>
internal sealed class NodeCoverage(string root, string folder, NodeRunner runner, string script, string? provider)
    : ICoverageEcosystem
{
    private static readonly Regex Assertion = CoverageTools.Pattern(@"\bassert\b|expect\(");
    private static readonly Regex Skip      = CoverageTools.Pattern(@"\.skip\(|\.todo\(|\{\s*skip\s*:|\{\s*todo\s*:|\bxit\(|\bxdescribe\(|\.fails\(");
    private static readonly Regex NothingRan = CoverageTools.Pattern(@"No test files found|No tests found|^ℹ tests 0\b|# tests 0\b");
    private static readonly Regex TestFile  = CoverageTools.Pattern(@"\.(test|spec)\.(m|c)?[jt]sx?$");

    public string Name => runner switch
    {
        NodeRunner.Vitest => $"vitest + coverage-{provider}",
        NodeRunner.Jest   => "jest --coverage",
        _                 => "node --test + c8",
    };

    public string FrameworkOf(string testPath) => runner switch
    {
        NodeRunner.Vitest => "vitest (import { describe, it, expect } from 'vitest')",
        NodeRunner.Jest   => "jest (describe, it and expect are globals)",
        _                 => "node:test with node:assert (import { test } from 'node:test'; import assert from 'node:assert')",
    };

    public async Task<CoverageRun> MeasureAsync(string? testFile, string outDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var include = folder.Length == 0 ? null : folder.TrimEnd('/') + "/**";
        List<string> args = runner switch
        {
            NodeRunner.Vitest => [script, "run", .. Optional(testFile), "--coverage.enabled", $"--coverage.provider={provider}",
                                  "--coverage.reporter=cobertura", $"--coverage.reportsDirectory={outDir}",
                                  .. include is null ? [] : new[] { $"--coverage.include={include}" }],
            // ⚠ The project's coverageThreshold off: one file never reaches it, and jest exits 1 for it, tests green.
            NodeRunner.Jest   => [script, .. Optional(testFile), "--ci", "--coverage", "--coverageReporters=cobertura",
                                  $"--coverageDirectory={outDir}", "--coverageThreshold={}",
                                  .. include is null ? [] : new[] { $"--collectCoverageFrom={include}" }],
            _                 => [script, "--reporter=cobertura", $"--report-dir={outDir}",
                                  .. include is null ? [] : new[] { $"--include={include}" }, "node", "--test", .. Optional(testFile)],
        };
        var (code, output, timedOut) = await CoverageTools.RunAsync(
            "node", args, root, testFile is null ? CoverageTools.SuiteDeadline : CoverageTools.FileRunDeadline, ct);
        return new CoverageRun(CoberturaReport.Read(Path.Combine(outDir, "cobertura-coverage.xml"), root),
                               Passed(code, output), output, timedOut);
    }

    private static IEnumerable<string> Optional(string? testFile) => testFile is null ? [] : [testFile];

    public async Task<(bool Passed, string Output, bool TimedOut)> RunAsync(string testFile, CancellationToken ct)
    {
        var (code, output, timedOut) = await CoverageTools.RunAsync("node", RunArgs(testFile), root, CoverageTools.FileRunDeadline, ct);
        return (Passed(code, output), output, timedOut);
    }

    /// <summary>One test file's run, coverage off.</summary>
    /// <remarks>⚠ A project that measures every run (collectCoverage, coverage.enabled) would rewrite its own coverage
    /// folder in the workspace, and fail its threshold, on each pass of a new file.</remarks>
    internal List<string> RunArgs(string testFile) => runner switch
    {
        NodeRunner.Vitest => [script, "run", testFile, "--coverage.enabled=false"],
        NodeRunner.Jest   => [script, "--ci", "--coverage=false", testFile],
        _                 => ["--test", testFile],
    };

    private static bool Passed(int code, string output) => code == 0 && !CoverageTools.Matches(NothingRan, output);

    /// <summary>Beside the project's existing tests — the folder most of them are in, with the suffix most of them have
    /// (<c>.spec.ts</c>, <c>.test.js</c>…) — or, when it has none, beside the source: where the runner collects it.</summary>
    /// <remarks>⚠ The suffix, not only the extension: a runner collects by its pattern (NestJS's jest: <c>*.spec.ts</c>), and
    /// a file it does not collect "has no tests" however good they are.</remarks>
    public string NewTestPath(string sourceFile)
    {
        var stem  = Path.GetFileNameWithoutExtension(sourceFile);
        var tests = WorkspaceScan.ShallowestFirst(WorkspaceScan.EnumerateFiles(root, "*.*").Where(f => CoverageTools.Matches(TestFile, f)))
                                 .Take(500).ToList();
        if (tests.Count == 0)
        {
            var beside = CodeActions.TestFilePathResolver.Resolve(sourceFile);   // foo.test.ts
            return CoverageTools.Unique(Path.Combine(Path.GetDirectoryName(beside) ?? root,
                                                     $"{stem}.coverage.test{Path.GetExtension(sourceFile)}"), File.Exists);
        }
        var dir = tests.GroupBy(f => Path.GetDirectoryName(f) ?? root, PathComparer.Default)
                       .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
        var suffix = tests.GroupBy(f => TestFile.Match(Path.GetFileName(f)).Value, StringComparer.OrdinalIgnoreCase)
                          .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
        return CoverageTools.Unique(Path.Combine(dir, $"{stem}.coverage{suffix}"), File.Exists);
    }

    public string Hint(string testPath) => string.Empty;

    public bool HasAssertion(string content) => CoverageTools.Matches(Assertion, content);

    public bool SkipsOrExpectsFailure(string content) => CoverageTools.Matches(Skip, content);

    /// <summary>The runner a <c>package.json</c> declares: vitest or jest in its dependencies, or <c>node --test</c> in its
    /// test script; null for any other.</summary>
    internal static NodeRunner? RunnerOf(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(packageJson);
            bool Has(string name) => doc.RootElement.TryGetProperty("devDependencies", out var dev) && dev.ValueKind == JsonValueKind.Object && dev.TryGetProperty(name, out _)
                                  || doc.RootElement.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object && deps.TryGetProperty(name, out _);
            if (Has("vitest")) return NodeRunner.Vitest;
            if (Has("jest"))   return NodeRunner.Jest;
            return doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object
                   && scripts.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.String
                   && test.GetString()!.Contains("node --test", StringComparison.Ordinal)
                ? NodeRunner.NodeTest : null;
        }
        catch (JsonException) { return null; }
    }
}
