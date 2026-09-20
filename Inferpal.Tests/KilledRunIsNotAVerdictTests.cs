using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A process killed at its budget is not a verdict — the rule <c>SmartFixValidator</c> states and
/// its two live siblings did not hold.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The expensive one is <c>run_tests</c>, because its wrong answer is <b>green</b>: the parsers
/// read a log, not the clock, so on a solution whose first project finishes and whose second hangs,
/// the killed run still carried <c>Passed! - Failed: 0, Passed: 16</c> — measured, the whole report
/// was <c>✓ PASSED — Failed: 0, Passed: 16, Skipped: 0, Total: 16</c>, and <c>/tdd</c> announced
/// success on a run where half the suite never started. The sentence the tool wrote two functions
/// earlier ("exceeded its budget and was stopped") never reached the reader: it was written INTO
/// the log the parser consumes, and the parser keeps summaries, not prose.
/// </para>
/// <para>
/// ⚠ <c>get_diagnostics</c> failed the same rule the other way round — measured,
/// <c>"Build : 1 erreur(s), 0 avertissement(s) — witherr.csproj"</c> on a build killed at 90 s,
/// read by <c>/fix-build</c> as <c>Errors</c>, i.e. up to five rounds of model patches against a
/// fragment. Its empty-output branch printed <c>-1</c>, which is <see cref="ChildProcess"/>'s
/// sentinel for "killed", as if it were the compiler's exit code.
/// </para>
/// </remarks>
public sealed class KilledRunIsNotAVerdictTests
{
    // ── run_tests ─────────────────────────────────────────────────────────────

    /// <summary>The log of a run whose first project finished and whose second was killed.</summary>
    private const string PartialLog = """
          Determining projects to restore...
        Test run for C:\repo\A.Tests\bin\Debug\net8.0\A.Tests.dll (.NETCoreApp,Version=v8.0)
        VSTest version 17.14.0 (x64)

        Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 1 s
        Test run for C:\repo\B.Tests\bin\Debug\net8.0\B.Tests.dll (.NETCoreApp,Version=v8.0)
        """;

    [Fact]
    public void AKilledRun_DoesNotHandBackThePassOfTheProjectsThatFinished()
    {
        // The parser still sees what it saw: a green summary. That is the point.
        var parsed = RunTestsTool.ParseDotnetOutput(PartialLog, -1);
        Assert.StartsWith("✓", parsed.TrimStart());   // witness: the false green is reachable

        var report = RunTestsTool.StoppedAtBudgetLine(300) + "\n\n" + parsed;

        Assert.False(TddCommandHandler.TestsPassed(report));
        Assert.True(TddCommandHandler.StoppedAtBudget(report));
        Assert.Contains("300", report);
    }

    /// <summary>The loop stops on that state, and says which one it is.</summary>
    [Fact]
    public void TheFourStatesAreDistinct()
    {
        var stopped = RunTestsTool.StoppedAtBudgetLine(120);

        Assert.True(TddCommandHandler.StoppedAtBudget(stopped));
        Assert.False(TddCommandHandler.NothingRan(stopped));
        Assert.False(TddCommandHandler.NothingRan(RunTestsTool.StoppedAtBudget));

        // …and the two states that existed before still answer for themselves.
        Assert.True(TddCommandHandler.NothingRan(RunTestsTool.NoTestMatchedFilter));
        Assert.True(TddCommandHandler.NothingRan(RunTestsTool.NothingProven));
        Assert.False(TddCommandHandler.StoppedAtBudget(RunTestsTool.NothingProven));
    }

    /// <summary>Reference arm: a run that finished is untouched — no note, and green stays green.</summary>
    [Fact]
    public void ARunThatFinished_IsStillReadAsItWas()
    {
        var parsed = RunTestsTool.ParseDotnetOutput(PartialLog, 0);

        Assert.True(TddCommandHandler.TestsPassed(parsed));
        Assert.False(TddCommandHandler.StoppedAtBudget(parsed));
    }

    /// <summary>
    /// End to end, on the real tool with a real runner: the fact reaches the report.
    /// </summary>
    /// <remarks>
    /// ⚠ Measured on the WHOLE path on purpose. The sentence already existed — RunProcessAsync
    /// wrote one — and died between the runner and its reader, because it was written into the log
    /// the parser consumes. A test on the pieces would have been green throughout.
    /// </remarks>
    [Fact]
    public async Task AKilledRunner_SaysSoInTheReportItself()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferpal-killed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var proj = Path.Combine(dir, "Hang.csproj");
            await File.WriteAllTextAsync(proj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <!-- ⚠ VSTest, measured: with no source files CoreCompile is skipped entirely,
                       and `dotnet test` reaches Build through an inner instance a BeforeTargets
                       hook on Build does not see — both returned in 0.8s and measured nothing. -->
                  <!-- ⚠ The stall only has to outlast the 5 s budget below, and it must NOT
                       outlast the suite: MSBuild runs this Exec from a node that DETACHES to be
                       reused, so the grandchild can sit outside the tree the timeout kills and
                       hold the inherited output pipe for as long as it sleeps. -->
                  <Target Name="Hang" BeforeTargets="VSTest;Build">
                    <Exec Command="sleep 20" Condition="'$(OS)' != 'Windows_NT'" />
                    <Exec Command="ping -n 20 127.0.0.1 &gt; nul" Condition="'$(OS)' == 'Windows_NT'" />
                  </Target>
                </Project>
                """);

            var args = JsonDocument.Parse(
                $$"""{"runner":"dotnet","path":{{JsonSerializer.Serialize(proj)}},"timeout_seconds":5}""").RootElement;

            var report = await new RunTestsTool(() => dir).ExecuteAsync(args, CancellationToken.None);

            Assert.StartsWith(RunTestsTool.StoppedAtBudget, report.TrimStart());
            Assert.Contains("5s", report);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ── get_diagnostics ───────────────────────────────────────────────────────

    private static ChildProcessResult Killed(string output) =>
        new(-1, output, string.Empty, TimedOut: true);

    [Fact]
    public void AKilledBuild_IsNotABuildThatHasNErrors()
    {
        const string partial = "Slow.cs(1,1): error CS9999: printed before the kill";

        var answer = GetDiagnosticsTool.Interpret(Killed(partial), "witherr.csproj", 90);

        Assert.Contains(Strings.DiagBuildStopped(90), answer);
        Assert.Contains(partial, answer);                                   // the fragment is kept
        Assert.Equal(GetDiagnosticsTool.BuildVerdict.NotBuilt,              // …and /fix-build stops
                     GetDiagnosticsTool.ReadVerdict(answer));
    }

    [Fact]
    public void AKilledBuildThatPrintedNothing_DoesNotShowTheKillSentinel()
    {
        var answer = GetDiagnosticsTool.Interpret(Killed(string.Empty), "quiet.csproj", 90);

        Assert.Contains(Strings.DiagBuildStopped(90), answer);
        Assert.DoesNotContain("-1", answer);
        Assert.Equal(GetDiagnosticsTool.BuildVerdict.NotBuilt, GetDiagnosticsTool.ReadVerdict(answer));
    }

    /// <summary>Reference arm: the two verdicts of a build that ran are unchanged.</summary>
    [Theory]
    [InlineData(0, "", "Clean")]
    [InlineData(1, "Foo.cs(3,9): error CS0103: nope", "Errors")]
    public void ABuildThatRan_KeepsItsVerdict(int exitCode, string output, string expected)
    {
        var run = new ChildProcessResult(exitCode, output, string.Empty, TimedOut: false);

        var answer = GetDiagnosticsTool.Interpret(run, "ran.csproj", 90);

        Assert.Equal(Enum.Parse<GetDiagnosticsTool.BuildVerdict>(expected),
                     GetDiagnosticsTool.ReadVerdict(answer));
        Assert.DoesNotContain(Strings.DiagBuildStopped(90), answer);
    }
}
