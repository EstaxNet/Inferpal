using System.IO;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Commands;
using Inferpal.Services.Presentation;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Every turn with tools carries their definitions — about half of an 8 192-token window — and the measure the pre-send
/// check, the Visual Studio gauge and the X-Ray read is the conversation alone: after a turn with tools both front-ends
/// replace the server's figure by an estimate of the history. The check therefore compacted only once the backend had
/// already refused the request, five times in a row on a growing conversation, and the X-Ray read "2 %" of a window the
/// next request filled at 55 %.
/// </summary>
public class ToolDefinitionsInContextTests
{
    private static InferpalConfig Config() => new()
    {
        ContextWindowSize        = 1000,
        ContextWindowKeepTurns   = 2,
        CompactionEnabled        = true,
        KvCacheAnchorMessages    = 0,
        CompactionTimeoutSeconds = 10,
    };

    private static List<ChatMessageDto> LongHistory()
    {
        var h = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 20; i++)
        {
            h.Add(new ChatMessageDto("user", $"question {i}"));
            h.Add(new ChatMessageDto("assistant", $"answer {i}"));
        }
        return h;
    }

    [Fact]
    public async Task AConversationThatFits_ButNotWithTheTools_IsCompacted()
    {
        // 600 of a 1 000-token window is under the 80 % trigger; with 400 tokens of tool definitions it is the whole window.
        var client = new FakeInferenceProvider { ChatResult = new("the summary", null, 0, 0) };

        var decision = await ContextManager.PrepareAsync(LongHistory(), Config(), client, lastPromptTokens: 600,
                                                         onStep: null, CancellationToken.None, toolTokens: 400);

        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);
    }

    [Fact]
    public async Task TheSameConversation_WithoutTools_IsLeftAlone()
    {
        // Reference arm: nothing to compact when the turn carries no tool — `/tools off`, a code action.
        var decision = await ContextManager.PrepareAsync(LongHistory(), Config(), new FakeInferenceProvider(),
                                                         lastPromptTokens: 600, onStep: null, CancellationToken.None,
                                                         toolTokens: 0);

        Assert.Equal(ContextOutcome.None, decision.Outcome);
    }

    [Fact]
    public void NothingMeasuredYet_IsNotTheToolsAlone()
    {
        // 0 is a first turn's state, and stays "nothing to decide on" — tools alone never trigger a compaction.
        Assert.Equal(0, ContextManager.NextTurnLoad(0, 4_000));
        Assert.Equal(5_000, ContextManager.NextTurnLoad(1_000, 4_000));
    }

    [Fact]
    public void TheXRay_CountsTheToolDefinitions_InTheWindowItShows()
    {
        var sections = new List<PromptSection> { new(PromptSectionKind.Base, null, new string('x', 400)) };   // ~100 tokens

        var with    = XRayCommandHandler.Handle(sections, historyTokens: 100, contextWindow: 1000, new InferpalConfig(),
                                                toolTokens: 500);
        var without = XRayCommandHandler.Handle(sections, historyTokens: 100, contextWindow: 1000, new InferpalConfig());

        Assert.Contains(Strings.XrayTools($"~{500:N0}"), with);
        Assert.Contains(Strings.XrayBudget($"~{700:N0}", $"{1000:N0}", "70"), with);
        // Reference arm: no tools, no line, and the fill is the prompt and the history.
        Assert.DoesNotContain(Strings.XrayTools("~0"), without);
        Assert.Contains(Strings.XrayBudget($"~{200:N0}", $"{1000:N0}", "20"), without);
    }

    [Fact]
    public void ThePanel_CountsTheToolDefinitions_InItsFill()
    {
        var sections = new List<PromptSection> { new(PromptSectionKind.Base, null, new string('x', 400)) };

        var model = XRayPanelPresenter.Build(sections, null, historyTokens: 100, contextWindow: 1000, toolTokens: 500);

        Assert.Equal(500, model.ToolTokens);
        Assert.Equal(70, model.FillPercent, precision: 1);
    }

    /// <summary>
    /// Every production site passes the tool definitions of the next turn: the parameter is optional (the tests of these
    /// entry points measure other things), so the compiler does not hold this — the scan does.
    /// </summary>
    [Theory]
    [InlineData("ContextManager.PrepareAsync(", "Inferpal.Host", "HostServer.cs")]
    [InlineData("ContextManager.PrepareAsync(", "Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs")]
    [InlineData("XRayPanelPresenter.Build(", "Inferpal.Host", "HostServer.cs")]
    [InlineData("XRayPanelPresenter.Build(", "Inferpal", "ToolWindow", "InferpalToolWindowData.Xray.cs")]
    [InlineData("XRayCommandHandler.Handle(", "Inferpal.Host", "HostSlashCommands.cs")]
    public void EveryProductionSite_PassesTheToolDefinitionsOfTheNextTurn(string marker, params string[] parts)
    {
        var code  = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));
        var sites = new List<string>();
        for (var at = code.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = code.IndexOf(marker, at + 1, StringComparison.Ordinal))
            sites.Add(code[at..code.IndexOf(';', at)]);

        Assert.NotEmpty(sites);                                                                    // WITNESS
        Assert.All(sites, call => Assert.Contains("toolTokens:", call, StringComparison.Ordinal));
    }

    [Fact]
    public void TheVisualStudioGauge_AddsTheToolDefinitions()
    {
        // The view model is not executable from this suite: its gauge is read in the source.
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(),
            "Inferpal", "ToolWindow", "InferpalToolWindowData.UiHelpers.cs"));
        var at = code.IndexOf("ContextBudgetGauge.Compute(", StringComparison.Ordinal);
        Assert.True(at >= 0, "the gauge moved: the scan reads nothing");                           // WITNESS

        Assert.Contains("NextTurnLoad(_lastPromptTokens, NextTurnToolTokens())", code[at..code.IndexOf(';', at)]);
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
    /// <summary>
    /// End to end in the host: a conversation under the trigger on its own, over it with the tool definitions the turn
    /// carries, is compacted before a turn with tools — and left alone with <c>/tools off</c> (reference arm).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATurnWithTools_IsCompacted_WhenTheConversationAndTheToolsOutgrowTheWindow(bool toolsOn)
    {
        using var h = CreateHarness(cfg => { cfg.ContextWindowSize = 8_192; cfg.CompactionEnabled = true; cfg.ContextWindowKeepTurns = 2; });
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var saved = new List<object>();
        for (var turn = 0; turn < 6; turn++)
        {
            saved.Add(new { role = "user",      content = $"question {turn}" });
            saved.Add(new { role = "assistant", content = $"answer {turn}" });
        }
        await h.Client.InvokeWithParameterObjectAsync<object?>("session/save", new { name = "tools", messages = saved })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeWithParameterObjectAsync<object?>("session/load", new { name = "tools" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var s     = h.Server.CurrentSession!;
        var tools = ContextManager.NextTurnToolTokens(s.Tools, toolsEnabled: true, planMode: false);
        Assert.True(tools > 1_000, $"the harness offers {tools} tokens of tool definitions: nothing to measure");   // witness
        s.ToolsEnabled     = toolsOn;
        s.LastPromptTokens = 8_192 * 8 / 10 - tools / 2;   // under the trigger alone, over it with the tools
        h.Fake.ChatResult  = new ChatTurnResult("the summary", null, 1, 1);

        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "next", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var compacted = s.History.Any(m => (m.Content ?? string.Empty).Contains("[Context Summary]", StringComparison.Ordinal));
        Assert.Equal(toolsOn, compacted);
    }
}
