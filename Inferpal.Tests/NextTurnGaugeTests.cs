using System.IO;
using Inferpal.Host;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

public partial class HostServerTests
{
    /// <summary>
    /// The VS Code context gauge showed the turn's own prompt measure — after an agent run, its last request, the run's
    /// internal transcript included (7 201 tokens, 88 %) — while the X-Ray it opens said what the next question sends
    /// (4 555, 56 %). The turn now reports the next question's figure, the one Visual Studio's gauge shows.
    /// </summary>
    [Fact]
    public async Task ATurn_ReportsWhatTheNextQuestionSends_NotItsOwnLastRequest()
    {
        using var h = CreateHarness(cfg => cfg.ContextWindowSize = 8_192);
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.AgentRunResult = new AgentResult("done", [], [], TokensUsed: 7_300, PromptTokens: 7_201);

        var result = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "fix the failing test", agentMode = false }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var s     = h.Server.CurrentSession!;
        var tools = ContextManager.NextTurnToolTokens(s.Tools, s.ToolsEnabled, s.PlanMode);
        Assert.True(tools > 1_000, "no tool definitions offered: the figure would not discriminate");   // witness
        Assert.Equal(7_201, result.PromptTokens);                                                      // the measure stays
        Assert.Equal(ContextManager.NextTurnLoad(s.LastPromptTokens, tools), result.NextTurnTokens);
        Assert.NotEqual(result.PromptTokens, result.NextTurnTokens);
    }
}

/// <summary>The VS Code half: the gauge reads the next question's figure first (the provider is not executable here).</summary>
public class NextTurnGaugeSourceTests
{
    [Fact]
    public void TheVsCodeGauge_ShowsTheNextQuestionsFigure()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(dir!.FullName, "vscode", "src", "chatViewProvider.ts")));

        Assert.Contains("this.promptTokens = ", code);                                                   // WITNESS
        Assert.Contains("this.promptTokens = result.nextTurnTokens || result.promptTokens", code);
    }
}
