using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A child a test parks on purpose must outlast the budget under test and <b>nothing else</b>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Measured, in CI</b>: the macOS leg's own cleanup reported
/// <c>Terminate orphan process: pid (25287) (sleep)</c> next to <c>(VBCSCompiler)</c> — a parked
/// child that had outlived the whole run, beside a build server that detaches by design. The worst
/// site asked for <c>sleep 600</c> to prove a <b>5 second</b> budget: ten minutes of stall bought
/// for five seconds of measurement.
/// </para>
/// <para>
/// ⚠ Why it is not merely untidy. A parked child inherits the output pipes of everything above it,
/// and an exited process is not a closed pipe: whoever reads that output waits for the <b>last
/// holder</b> to let go. So a stall that outlives the run keeps a dead step alive, which is why a
/// hung CI job produces no log and no <c>if: always()</c> step — the budget killed the process it
/// could see, and the pipe was held by one it could not. Worse, MSBuild runs an <c>Exec</c> from a
/// node that <b>detaches</b> to be reused, so the grandchild is routinely outside the process tree
/// the timeout kills: the tidy path cannot be relied on to reach it.
/// </para>
/// <para>
/// ⚠ The ceiling is on the <b>stall</b>, not on the assertion. A test whose time assertion sits
/// above its own child's sleep is met by the child dying of old age — the kill it exists to prove
/// need never have happened. Both ends have to stay apart, and only this one is mechanical.
/// </para>
/// </remarks>
public class OrphanedChildTests
{
    /// <summary>
    /// The longest a test may park a child. Every budget this suite measures is five seconds or
    /// less, so this leaves an ample margin while staying far under the run itself.
    /// </summary>
    private const int MaxStallSeconds = 30;

    /// <summary>The three ways this suite parks a child, across both shell dialects.</summary>
    private static readonly Regex Stall = new(
        @"\bsleep\s+(?<s>\d+)\b|Start-Sleep\s+-Seconds\s+(?<s>\d+)\b|\bping\s+-n\s+(?<s>\d+)\b",
        RegexOptions.Compiled);

    [Fact]
    public void NoTest_ParksAChildForLongerThanTheRunItself()
    {
        var offenders = new List<string>();
        var seen      = 0;

        foreach (var path in TestSources())
            foreach (Match m in Stall.Matches(ConventionCoverageTests.CodeOnly(path)))
            {
                seen++;
                var seconds = int.Parse(m.Groups["s"].Value);
                if (seconds > MaxStallSeconds)
                    offenders.Add($"{Path.GetFileName(path)}: `{m.Value.Trim()}`");
            }

        // Witness: the scan reads real sites. It stays well under the real count so that removing
        // one site trips the rule below and never this line.
        Assert.True(seen >= 5, $"the scan found only {seen} parked children — it is reading nothing");

        Assert.True(offenders.Count == 0,
            $"a test parks a child for more than {MaxStallSeconds}s; it can outlive the run and hold "
            + "its output pipe open, and an MSBuild node that detaches puts it out of reach of the "
            + "kill: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Every <c>.cs</c> of this suite, <b>with a positive witness</b>.
    /// </summary>
    /// <remarks>
    /// ⚠ Filtered on the extension rather than a <c>*.cs</c> glob: on Windows that pattern also
    /// matches short 8.3 names, so it is not the set it reads as.
    /// </remarks>
    private static IReadOnlyList<string> TestSources()
    {
        var dir = Path.Combine(RepoRoot(), "Inferpal.Tests");
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                             .Where(p => string.Equals(Path.GetExtension(p), ".cs", StringComparison.OrdinalIgnoreCase))
                             .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                      && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                             .ToList();

        Assert.True(files.Count > 50, $"only {files.Count} test sources found under {dir}");
        return files;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
