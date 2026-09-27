using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A test run whose code did not compile says so, with the compiler errors — and an SDK log line is
//  never a failing test.
//
//  Under --verbosity normal (run_tests' invocation) the .NET 10 SDK logs "Failed to load prune package
//  data from PrunePackageData folder, loading from targeting packs instead" once per project. The
//  failed-test parser took every line starting with "Failed" for a test: after an agent's rename left a
//  caller behind (CS1061), run_tests answered "Failing tests: ✗ to load prune package data…" and nothing
//  else, and the model concluded "the code compiles, the test runner has an unrelated issue". The lines
//  below are verbatim from real runs (SDK 10.0.401, xUnit), paths shortened.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class DotnetBuildFailureReportTests
{
    private const string Prune = """
             2>AddPrunePackageReferences:
                 Loading prune package data from PrunePackageData folder
                 Failed to load prune package data from PrunePackageData folder, loading from targeting packs instead
             3>AddPrunePackageReferences:
                 Loading prune package data from PrunePackageData folder
                 Failed to load prune package data from PrunePackageData folder, loading from targeting packs instead

        """;

    private const string BuildFailure = Prune + """
             3>/ws/tests/Shop.Tests/CartTests.cs(12,31): error CS1061: 'Cart' does not contain a definition for 'ComputeTotal' and no accessible extension method 'ComputeTotal' accepting a first argument of type 'Cart' could be found (are you missing a using directive or an assembly reference?) [/ws/tests/Shop.Tests/Shop.Tests.csproj]
                 CompilerServer: server - server processed compilation - Shop.Tests (net10.0)
             3>Done Building Project "/ws/tests/Shop.Tests/Shop.Tests.csproj" (default targets) -- FAILED.
             1>Done Building Project "/ws/Shop.slnx" (VSTest target(s)) -- FAILED.

        Build FAILED.

               (CoreCompile target) ->
                 /ws/tests/Shop.Tests/CartTests.cs(12,31): error CS1061: 'Cart' does not contain a definition for 'ComputeTotal' and no accessible extension method 'ComputeTotal' accepting a first argument of type 'Cart' could be found (are you missing a using directive or an assembly reference?) [/ws/tests/Shop.Tests/Shop.Tests.csproj]

            0 Warning(s)
            1 Error(s)
        """;

    private const string TestFailure = Prune + """
          Passed PricingTests.ApplyDiscount_ZeroPercent_ReturnsSamePrice [6 ms]
          Passed CartTests.Total_SumsThePrices [6 ms]
          Failed PricingTests.ApplyDiscount_TenPercent_OnHundred_ReturnsNinety [3 ms]
          Error Message:
           Assert.Equal() Failure: Values differ
        Expected: 90
        Actual:   0
          Stack Trace:
             at PricingTests.ApplyDiscount_TenPercent_OnHundred_ReturnsNinety() in /ws/tests/Shop.Tests/PricingTests.cs:line 7

        Test Run Failed.
        Total tests: 3
             Passed: 2
             Failed: 1
         Total time: 0.6145 Seconds
               _VSTestConsole:
                 MSB4181: The "VSTestTask" task returned false but did not log an error.

        Build FAILED.
            0 Warning(s)
            0 Error(s)
        """;

    private const string Pass = Prune + """
        Test Run Successful.
        Total tests: 3
             Passed: 3
         Total time: 0.5 Seconds
        """;

    [Fact]
    public void ABuildThatDidNotCompile_IsReportedWithItsErrors_AndNoPhantomTest()
    {
        var report = RunTestsTool.ParseDotnetOutput(BuildFailure, exitCode: 1);

        Assert.StartsWith(RunTestsTool.BuildFailed, report);
        Assert.Contains("CartTests.cs(12,31): error CS1061: 'Cart' does not contain a definition for 'ComputeTotal'", report);
        Assert.Single(RunTestsTool.CompileErrors(BuildFailure));   // printed twice by MSBuild, reported once
        Assert.DoesNotContain("prune package data", report);
        Assert.DoesNotContain("Shop.Tests.csproj]", report);        // MSBuild's project suffix is not part of the error
    }

    [Fact]
    public void AFailingTest_IsStillATestFailure_NotABuildFailure()
    {
        // Reference arm: `dotnet test` also prints "Build FAILED." when a test fails — the summary decides.
        var report = RunTestsTool.ParseDotnetOutput(TestFailure, exitCode: 1);

        Assert.StartsWith("✗ FAILED — Failed: 1, Passed: 2", report);
        Assert.Contains("✗ PricingTests.ApplyDiscount_TenPercent_OnHundred_ReturnsNinety", report);
        Assert.Contains("Expected: 90", report);
        Assert.DoesNotContain("prune package data", report);
        Assert.DoesNotContain(RunTestsTool.BuildFailed, report);
    }

    [Fact]
    public void APassingRun_ListsNoFailure()
    {
        var report = RunTestsTool.ParseDotnetOutput(Pass, exitCode: 0);

        Assert.StartsWith("✓ PASSED", report);
        Assert.DoesNotContain("Failing tests", report);
    }
}
