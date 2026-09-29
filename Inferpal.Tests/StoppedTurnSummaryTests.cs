using System.IO;
using Inferpal.Host;
using StreamJsonRpc;
using Inferpal.Localization;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The basic loop answers EMPTY when it stops a model that repeats its tool calls after work was done, so the tool
/// summary is exactly what a stopped turn shows — and "✓ Done — &lt;tools&gt;" above "the agent was repeating the same
/// tool calls and was stopped" is the product contradicting itself. The check mark is only for a turn that ended
/// cleanly; with an end notice under it, the line only names the tools.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares localized sentences
public class StoppedTurnSummaryTests
{
    private static readonly List<ToolExecution> FiveCommands =
        Enumerable.Range(0, 5).Select(_ => new ToolExecution("run_command", "{\"command\":\"dotnet --version\"}", "8.0.100"))
                  .ToList();

    [Fact]
    public void AStoppedTurn_NamesTheToolsWithoutACheckMark()
    {
        var shown = ChatTurnPolicy.ToolSummaryAnswer(FiveCommands, Strings.AgentEndedOnRepeat);

        Assert.Equal(Strings.MsgAgentToolsCalled("run_command ×5"), shown);
        Assert.DoesNotContain("✓", shown);
    }

    /// <summary>Every end notice counts, not only the loop: edits that did not land are not "done" either.</summary>
    [Fact]
    public void EditsThatDidNotLand_AreNotDoneEither()
    {
        var edits = new List<ToolExecution> { new("write_file", "{}", "Error: not read first") };

        Assert.Equal(Strings.MsgAgentToolsCalled("write_file"),
                     ChatTurnPolicy.ToolSummaryAnswer(edits, Strings.AgentEditsNotApplied));
    }

    /// <summary>Reference arm: a turn that ended cleanly keeps its "✓ Done" — the fix must not strip it everywhere.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ACleanTurn_KeepsItsCheckMark(string? endNotice) =>
        Assert.Equal(Strings.MsgAgentDone("run_command ×5"), ChatTurnPolicy.ToolSummaryAnswer(FiveCommands, endNotice));

    /// <summary>The ten languages carry the neutral line, and none of them puts a check mark in it.</summary>
    [Fact]
    public void TheNeutralLine_HasNoCheckMarkInAnyLanguage()
    {
        var dir   = Path.Combine(RepoRoot(), "Inferpal.Core", "Localization");
        var files = Directory.GetFiles(dir, "Strings*.resx");
        Assert.Equal(10, files.Length);                                         // WITNESS: the ten files were read
        foreach (var file in files)
        {
            var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(file),
                "<data name=\"MsgAgentToolsCalled\" xml:space=\"preserve\"><value>(.*?)</value>");
            Assert.True(match.Success, $"MsgAgentToolsCalled missing from {Path.GetFileName(file)}");
            Assert.Contains("{0}", match.Groups[1].Value, StringComparison.Ordinal);
            Assert.DoesNotContain("✓", match.Groups[1].Value, StringComparison.Ordinal);
        }
    }

    /// <summary>The Visual Studio window takes the line from the policy, with the notice it shows under it.</summary>
    [Fact]
    public void TheVisualStudioWindow_PassesItsEndNotice()
    {
        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(
            RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"));

        Assert.Contains("FinalAnswerKind.ToolSummary", vm, StringComparison.Ordinal);   // WITNESS: the fallback site
        Assert.Contains("ChatTurnPolicy.ToolSummaryAnswer(agentExecutions, agentEndNotice)", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("Strings.MsgAgentDone(", vm, StringComparison.Ordinal);
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

public partial class HostServerTests
{
    private static AgentResult RanFiveCommands(bool loop) => new(
        string.Empty,
        Enumerable.Range(0, 5).Select(_ => new ToolExecution("run_command", "{}", "8.0.100")).ToList(),
        [], WasLoopDetected: loop);

    /// <summary>The VS Code screen: a turn stopped for repeating names its tools, and the notice says why it stopped.</summary>
    [Fact]
    public async Task ChatSend_ATurnStoppedForRepeating_HasNoDoneCheckMark()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.AgentRunResult = RanFiveCommands(loop: true);

        var result = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "which .NET SDK?", agentMode = false }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(Strings.AgentEndedOnRepeat, result.EndNotice);
        Assert.Equal(Strings.MsgAgentToolsCalled("run_command ×5"), result.Text);
    }

    /// <summary>Reference arm: the same tools, a clean end — the check mark stays.</summary>
    [Fact]
    public async Task ChatSend_ACleanTurnWithoutText_KeepsItsDoneCheckMark()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.AgentRunResult = RanFiveCommands(loop: false);

        var result = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "which .NET SDK?", agentMode = false }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Null(result.EndNotice);
        Assert.Equal(Strings.MsgAgentDone("run_command ×5"), result.Text);
    }
}
