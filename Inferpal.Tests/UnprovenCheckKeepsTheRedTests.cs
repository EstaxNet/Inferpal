using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A check that proved nothing — a test run filtered on a name that no longer exists, a build that found no project —
/// was taken as the turn's last word: the red run before it was erased, and no "the last test run failed" notice
/// followed an answer that left the tests failing.
/// </summary>
public class UnprovenCheckKeepsTheRedTests
{
    private static ToolExecution Exec(string name, string output, string input = "{}") => new(name, input, output);

    private const string RedRun = "✗ FAILED — Failed: 1, Passed: 3, Skipped: 0, Total: 4\n\nFailing tests:\n  Cart.Total";

    [Fact]
    public void ARedRunFollowedByARunThatMatchedNothing_IsStillRed()
    {
        var runs = new[]
        {
            Exec("run_tests", RedRun),
            Exec("apply_diff", "Diff applied to Cart.cs."),
            Exec("run_tests", RunTestsTool.NoTestMatchedFilter + "\nFilter: CartTotalRenamed"),
        };

        Assert.True(ChatTurnPolicy.LastCheckFailed(runs, filesChangedInRun: 1));
    }

    [Fact]
    public void ARedRunFollowedByABuildThatFoundNoProject_IsStillRed()
    {
        var runs = new[]
        {
            Exec("run_tests", RedRun),
            Exec("get_diagnostics", "No .sln, .slnx or project file found."),
        };

        Assert.True(ChatTurnPolicy.LastCheckFailed(runs, filesChangedInRun: 1));
    }

    [Fact]
    public void AGreenRunAfterTheRedOne_StillWins()
    {
        // Reference arm: a later run that proved something is the last word.
        var runs = new[]
        {
            Exec("run_tests", RedRun),
            Exec("run_tests", "✓ PASSED — Failed: 0, Passed: 4, Skipped: 0, Total: 4"),
        };

        Assert.False(ChatTurnPolicy.LastCheckFailed(runs, filesChangedInRun: 1));
    }
}
