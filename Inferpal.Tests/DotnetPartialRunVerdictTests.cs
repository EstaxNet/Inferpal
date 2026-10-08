using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A test summary is the verdict of the test projects that RAN, never the verdict of the run.
//
//  In a solution with two test projects, `dotnet test` still runs the project that compiles when the
//  other does not — "Test Run Successful", exit code 1 — and a project whose test host crashes prints
//  "Test Run Aborted." with no totals at all. Read from the totals alone, both runs were "✓ PASSED":
//  /tdd declared victory on round 1 over a test that never compiled, or that blew the stack. The
//  outputs below are verbatim from real runs (SDK 10, xUnit 2.9, solution Shop.slnx with Shop.Unit and
//  Shop.Integration), paths shortened.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class DotnetPartialRunVerdictTests
{
    private const string OneProjectDidNotCompile = """
             3>/ws/Shop.Unit/UnitTest1.cs(6,62): error CS0246: The type or namespace name 'Cart' could not be found (are you missing a using directive or an assembly reference?) [/ws/Shop.Unit/Shop.Unit.csproj]
             3>Done Building Project "/ws/Shop.Unit/Shop.Unit.csproj" (VSTest target(s)) -- FAILED.
        Test run for /ws/Shop.Integration/bin/Debug/net10.0/Shop.Integration.dll (.NETCoreApp,Version=v10.0)
        A total of 1 test files matched the specified pattern.
          Passed Shop.Integration.UnitTest1.Test1 [2 ms]

        Test Run Successful.
        Total tests: 1
             Passed: 1
         Total time: 0.5108 Seconds
             1>Done Building Project "/ws/Shop.slnx" (VSTest target(s)) -- FAILED.

        Build FAILED.

               "/ws/Shop.slnx" (VSTest target) (1) ->
               "/ws/Shop.Unit/Shop.Unit.csproj" (VSTest target) (3) ->
               (CoreCompile target) ->
                 /ws/Shop.Unit/UnitTest1.cs(6,62): error CS0246: The type or namespace name 'Cart' could not be found (are you missing a using directive or an assembly reference?) [/ws/Shop.Unit/Shop.Unit.csproj]

            0 Warning(s)
            1 Error(s)
        """;

    private const string OneTestHostCrashed = """
        Test run for /ws/Shop.Integration/bin/Debug/net10.0/Shop.Integration.dll (.NETCoreApp,Version=v10.0)
          Passed Shop.Integration.UnitTest1.Test1 [2 ms]

        Test Run Successful.
        Total tests: 1
             Passed: 1
         Total time: 0.4920 Seconds
        Test run for /ws/Shop.Unit/bin/Debug/net10.0/Shop.Unit.dll (.NETCoreApp,Version=v10.0)
        Stack overflow.
        Repeated 24027 times:
        --------------------------------
           at Shop.Unit.CartTests.Depth(Int32)
        --------------------------------
        The active test run was aborted. Reason: Test host process crashed : Stack overflow.

        Test Run Aborted.
             3>_VSTestConsole:
                 MSB4181: The "VSTestTask" task returned false but did not log an error.
             1>Done Building Project "/ws/Shop.slnx" (VSTest target(s)) -- FAILED.

        Build FAILED.
            0 Warning(s)
            0 Error(s)
        """;

    private const string BothPassed = """
          Passed Shop.Integration.UnitTest1.Test1 [2 ms]

        Test Run Successful.
        Total tests: 1
             Passed: 1
          Passed Shop.Unit.CartTests.A_Total [2 ms]

        Test Run Successful.
        Total tests: 1
             Passed: 1
        """;

    [Fact]
    public void AProjectThatDidNotCompile_IsRed_EvenWhenAnotherProjectsTestsPassed()
    {
        var report = RunTestsTool.ParseDotnetOutput(OneProjectDidNotCompile, exitCode: 1);

        Assert.StartsWith(RunTestsTool.PartlyBuilt, report);
        Assert.Contains("error CS0246: The type or namespace name 'Cart' could not be found", report);
        Assert.Contains("Passed: 1", report);                                 // what did run is still said
        Assert.False(TddCommandHandler.TestsPassed(report));
        Assert.True(TddCommandHandler.TestsFailed(report));
    }

    [Fact]
    public void ACrashedTestHost_IsRed_WithItsReason_EvenWhenAnotherProjectsTestsPassed()
    {
        var report = RunTestsTool.ParseDotnetOutput(OneTestHostCrashed, exitCode: 1);

        Assert.StartsWith(RunTestsTool.TestRunAborted, report);
        Assert.Contains("Test host process crashed : Stack overflow.", report);
        Assert.True(TddCommandHandler.TestsFailed(report));
    }

    [Fact]
    public void ACrashedTestHost_AloneInTheRun_IsRedToo()
    {
        var alone = OneTestHostCrashed[OneTestHostCrashed.IndexOf("Test run for /ws/Shop.Unit", StringComparison.Ordinal)..];

        var report = RunTestsTool.ParseDotnetOutput(alone, exitCode: 1);

        Assert.StartsWith(RunTestsTool.TestRunAborted, report);
        Assert.True(TddCommandHandler.TestsFailed(report));
    }

    [Fact]
    public void AGreenSummaryUnderAFailingExitCode_IsNotGreen()
    {
        // No compiler error and no crash, and still exit code 1 (a coverage threshold, a failing MSBuild step):
        // the run failed, whatever the tests that ran say.
        var report = RunTestsTool.ParseDotnetOutput(BothPassed + "\nerror : The total line coverage is below the specified 80\n",
                                                    exitCode: 1);

        Assert.StartsWith("✗", report);
        Assert.Contains("exited with code 1", report);
        Assert.False(TddCommandHandler.TestsPassed(report));
    }

    [Fact]
    public void TwoProjectsThatPassed_ArePassed()
    {
        // Reference arm: the same summaries under exit code 0 are the green they always were.
        var report = RunTestsTool.ParseDotnetOutput(BothPassed, exitCode: 0);

        Assert.StartsWith("✓ PASSED — Failed: 0, Passed: 2, Skipped: 0, Total: 2", report);
        Assert.True(TddCommandHandler.TestsPassed(report));
    }
}
