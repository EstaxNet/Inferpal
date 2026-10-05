using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ <c>/fix-build</c> (Visual Studio's build banner, "Fix with AI") ignored a FAILED model run — a backend down, a
/// circuit open, a wrong key: it rebuilt the solution round after round against a backend that had already said no, and
/// ended on "build still failing after 5 attempts — manual intervention required", blaming the code when the model had
/// never run. And it sent the tool loop to the chat model, not the agent role's. <c>/tdd</c>, its sibling, does both
/// right (<c>TddCommandHandler</c>). The view model is not executable here: the rules read it.
/// </summary>
public class FixBuildFailedRunTests
{
    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return ConventionCoverageTests.CodeOnly(
            Path.Combine(dir!.FullName, "Inferpal", "ToolWindow", "InferpalToolWindowData.PromptHistory.cs"));
    }

    [Fact]
    public void AFailedRun_EndsTheLoop()
    {
        var code = Source();
        Assert.Contains("private async Task<bool> RunFixIterationAsync(", code, StringComparison.Ordinal);
        Assert.Contains("if (!await RunFixIterationAsync(buildOutput, round, tok)) return;", code, StringComparison.Ordinal);
        Assert.Contains("return !result.Failed;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFixLoop_RunsOnTheAgentModel()
    {
        var code = Source();
        var start = code.IndexOf("private async Task<bool> RunFixIterationAsync(", StringComparison.Ordinal);
        Assert.True(start > 0, "RunFixIterationAsync moved: the rule measures nothing");   // WITNESS
        var body = code[start..code.IndexOf("return !result.Failed;", start, StringComparison.Ordinal)];
        Assert.Contains("ModelRouter.Resolve(_config, ModelRole.Agent)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_config.DefaultModel", body, StringComparison.Ordinal);
    }
}
