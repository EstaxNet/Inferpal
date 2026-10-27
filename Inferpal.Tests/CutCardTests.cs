using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A turn cut because the server stayed silent past its announced bound says so to the screens: the failure carries
/// it (<c>WaitRanOut</c>), and the card under its cause offers the time limits and says when nothing was written.
/// </summary>
public class CutCardTests
{
    private static AgentHttpException Silent() =>
        new("No answer after 10 min.", isTimeout: true, prefillOverran: true);

    [Fact]
    public void TheCard_SaysNothingWritten_OnlyForStepsThatWroteNothing()
    {
        string[] reads = ["search_in_files", "read_file"];
        Assert.Null(CutCard.Of(waitRanOut: false, reads, filesChanged: 0));           // another failure: its own cause
        Assert.Equal(Strings.CutNothingWritten, CutCard.Of(true, reads, filesChanged: 0)!.Note);
        Assert.Null(CutCard.Of(true, [], filesChanged: 0)!.Note);                       // a plain question: no steps
        Assert.Null(CutCard.Of(true, [.. reads, "write_file"], filesChanged: 1)!.Note); // its result bar says what it wrote
        // A command, a server's tool: they may have written what the run cannot count.
        Assert.Null(CutCard.Of(true, [.. reads, "run_command"], filesChanged: 0)!.Note);
        Assert.Null(CutCard.Of(true, ["github__create_issue"], filesChanged: 0)!.Note);
        Assert.Equal(Strings.CutOpenTimeLimits, CutCard.Of(true, [], 0)!.OpenLimits);
    }

    [Fact]
    public void TheTimeLimitsPage_IsTheSchemasPageThatHoldsThem()
    {
        var page = Assert.Single(SettingsSchema.Tabs, t => t.Key == CutCard.TimeLimitsPage);
        var section = Assert.Single(page.Sections, s => s.Title == CutCard.TimeLimitsSection);
        Assert.Contains(section.Fields, f => f.Key == "normalTimeoutSeconds");
    }

    private sealed class SilentClient(InferpalConfig config, Exception failure) : OllamaClient(config)
    {
        public override Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            ReasoningEffort effort, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
            string? toolChoice = null, Action<string>? onThinking = null, Action<ModelWait?>? onWaiting = null)
            => throw failure;
    }

    [Fact]
    public async Task TheBasicLoop_CarriesAWaitThatRanOut_AndNoOtherFailure()
    {
        var silent = await new SilentClient(new InferpalConfig(), Silent())
            .RunAgentAsync("m", [new("user", "hi")], EmptyToolRegistry.Instance, _ => { }, null, CancellationToken.None);
        var refused = await new SilentClient(new InferpalConfig(), new AgentHttpException("refused", isTimeout: false))
            .RunAgentAsync("m", [new("user", "hi")], EmptyToolRegistry.Instance, _ => { }, null, CancellationToken.None);

        Assert.True(silent.Failed);
        Assert.True(silent.WaitRanOut);
        Assert.True(refused.Failed);
        Assert.False(refused.WaitRanOut);
    }

    [Fact]
    public async Task TheOrchestrator_CarriesAWaitThatRanOut_FromItsPlan()
    {
        var client = new FakeInferenceProvider { OnChatRequest = (_, _, _, _) => throw Silent() };

        var result = await new AgentOrchestrator(client, new InferpalConfig { CompactionEnabled = false })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "What does Cart do?")], tools: EmptyToolRegistry.Instance,
                      onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
                      onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

        Assert.True(result.Failed);
        Assert.True(result.WaitRanOut);
    }
}

public partial class HostServerTests
{
    [Fact]
    public async Task ChatSend_AnAgentTurnCutAtItsBound_CarriesItsCutCard()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        h.Fake.OnChatRequest = (_, _, _, _) => throw new AgentHttpException("No answer after 10 min.", isTimeout: true, prefillOverran: true);

        var result = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "hi", agentMode = true })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.NotNull(result.Error);
        Assert.Equal(CutCard.TimeLimitsPage, result.Cut!.Page);
        Assert.Equal(Strings.CutOpenTimeLimits, result.Cut.OpenLimits);
    }

    [Fact]
    public async Task ChatSend_AnotherFailure_HasNoCutCard()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        h.Fake.OnChatRequest = (_, _, _, _) => throw new AgentHttpException("refused", isTimeout: false);

        var result = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "hi", agentMode = true })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.NotNull(result.Error);
        Assert.Null(result.Cut);
    }
}
