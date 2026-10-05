using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Localization;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Visual Studio's "the last build failed — N error(s)" banner said three false things. N was the number of LINES
/// the in-process side carried, capped at 30, so 200 errors read as 30; when the Error List had not been filled in
/// time it read "0 error(s)" under "the build failed"; and nothing ever said a build had SUCCEEDED, so the banner of a
/// fixed build stayed up for good. The count now travels with the lines, an uncounted failure names no number, and a
/// success clears the banner.
/// </summary>
[Collection(SignalCollection.Name)]
public class BuildBannerTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void TheCount_IsTheBuilds_NotTheNumberOfLinesCarried()
    {
        var lines = Enumerable.Range(1, 30).Select(i => $"F.cs({i},1): error CS0103: x{i}").ToList();
        BuildSignalFile.Write(@"C:\work\Sln.sln", lines, errorCount: 200);

        var payload = BuildSignalFile.TryRead();
        Assert.Equal(30, payload.ErrorLines.Length);   // witness: the lines are capped
        Assert.Equal(200, payload.ErrorCount);

        var monitor = new VsBuildMonitor();
        int? announced = null;
        monitor.BuildFailed += (count, _) => announced = count;
        monitor.Dispatch(payload);
        Assert.Equal(200, announced);
    }

    [Fact]
    public void AWriterThatDidNotCount_IsReadFromItsLines()
    {
        // Reference arm: the previous payload shape (no count) still announces what it carries.
        BuildSignalFile.Write(@"C:\work\Sln.sln", ["A.cs(1,1): error CS0103: nope", "B.cs(2,2): error CS1002: ;"]);
        Assert.Equal(2, BuildSignalFile.TryRead().ErrorCount);
    }

    [Fact]
    public void ASuccess_ClearsTheBanner_AndRaisesNoFailure()
    {
        BuildSignalFile.Write(@"C:\work\Sln.sln", errorLines: null, errorCount: 0, succeeded: true);
        var payload = BuildSignalFile.TryRead();
        Assert.True(payload.Succeeded);

        var monitor   = new VsBuildMonitor();
        var failed    = false;
        var succeeded = false;
        monitor.BuildFailed    += (_, _) => failed = true;
        monitor.BuildSucceeded += () => succeeded = true;
        monitor.Dispatch(payload);

        Assert.True(succeeded);
        Assert.False(failed);
    }

    [Fact]
    public void AnUncountedFailure_NamesNoNumber()
    {
        Assert.DoesNotContain("0", Strings.WelcomeBuildFailedUncounted, StringComparison.Ordinal);

        var chrome = ConventionCoverageTests.CodeOnly(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(),
                                                                    "Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"));
        Assert.Contains("_buildErrorCount > 0 ? Strings.WelcomeBuildFailed(_buildErrorCount) : Strings.WelcomeBuildFailedUncounted",
                        chrome, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindow_HidesTheBanner_OnASuccess()
    {
        var dir  = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow");
        Assert.Contains("_buildMonitor.BuildSucceeded += OnVsBuildSucceeded;",
                        ConventionCoverageTests.CodeOnly(Path.Combine(dir, "InferpalToolWindowData.Construction.cs")), StringComparison.Ordinal);

        var actions   = ConventionCoverageTests.CodeOnly(Path.Combine(dir, "InferpalToolWindowData.CodeActions.cs"));
        var succeeded = actions[actions.IndexOf("private void OnVsBuildSucceeded()", StringComparison.Ordinal)..];
        succeeded     = succeeded[..succeeded.IndexOf("private ", 10, StringComparison.Ordinal)];
        Assert.Contains("HasBuildFailedBanner   = false;", succeeded, StringComparison.Ordinal);
    }

    /// <summary>The in-process side counts every error, carries at most its line cap, and says a success.</summary>
    [Fact]
    public void TheInProcessSide_CountsEveryError_AndSaysASuccess()
    {
        var handler = ConventionCoverageTests.CodeOnly(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(),
                                                                     "Inferpal.InProc", "GhostText", "VsBuildEventHandler.cs"));
        var collect = handler[handler.IndexOf("private (List<string> Lines, int Count) CollectBuildErrors()", StringComparison.Ordinal)..];
        Assert.DoesNotContain("&& result.Count < ", collect, StringComparison.Ordinal);   // the loop is not stopped by the line cap
        Assert.Matches(@"count\+\+;\s*if \(result\.Count >= MaxErrorLines\) continue;", collect);

        Assert.Contains("BuildSignalFile.Write(solutionPath!, errorLines: null, errorCount: 0, succeeded: true)", handler,
                        StringComparison.Ordinal);
    }
}
