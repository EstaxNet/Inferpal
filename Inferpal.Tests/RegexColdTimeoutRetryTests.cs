using System;
using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A short regex budget expires on an ORDINARY pattern when a garbage collection lands in the first match of a freshly
/// built regex; the match is run once more before it counts as a timeout.
/// </summary>
/// <remarks>
/// ⚠ Measured under heavy collection: 6 false "no match" in 120,105 first matches of new globs, none in 9 million warm
/// ones. The rule was not applied, the index exclusion not honoured, the approval forced to a prompt — each said once in
/// /diagnostics as a pattern "that timed out". The clock cannot be driven from a test, so the retry is tested on its
/// core, and the three short-budget readers are held to it by name.
/// </remarks>
public class RegexColdTimeoutRetryTests
{
    [Fact]
    public void AMatchThatTimesOutOnce_IsRunAgain_AndItsAnswerCounts()
    {
        var calls = 0;
        var matched = RegexBudget.RetryOnce(() => ++calls == 1 ? throw new RegexMatchTimeoutException() : true);

        Assert.True(matched);
        Assert.Equal(2, calls);
    }

    /// <summary>⚠ Reference arms: a pattern that times out twice is still a timeout, and a match that answers is run once.</summary>
    [Fact]
    public void ATimeoutTwice_IsStillATimeout_AndAnAnswerIsNotRunAgain()
    {
        Assert.Throws<RegexMatchTimeoutException>(() => RegexBudget.RetryOnce(() => throw new RegexMatchTimeoutException()));

        var calls = 0;
        Assert.False(RegexBudget.RetryOnce(() => { calls++; return false; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void APathologicalPattern_StillTimesOut_ThroughTheSharedReader()
    {
        var rx = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(20));

        Assert.Throws<RegexMatchTimeoutException>(() => RegexBudget.IsMatch(rx, new string('a', 40) + "!"));
    }

    [Theory]
    [InlineData("Governance", "RulesService.cs")]
    [InlineData("Rag", "IndexExclusions.cs")]
    [InlineData("Execution", "PermissionPolicy.cs")]
    public void TheShortBudgetReaders_MatchThroughIt(string folder, string file)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), "Inferpal.Core", "Services", folder, file));

        Assert.Contains("RegexBudget.IsMatch(", code);
        Assert.Equal(code.Split(".IsMatch(").Length - 1, code.Split("RegexBudget.IsMatch(").Length - 1);
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
