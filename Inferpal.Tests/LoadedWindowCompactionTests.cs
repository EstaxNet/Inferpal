using System.IO;
using Inferpal.Config;
using Inferpal.Host;
using StreamJsonRpc;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Compaction measures the conversation against the window it believes the model has — and it
/// believed the CONFIGURED one. LM Studio loads a model with a window of its own choosing (4,096 is a
/// common default, Inferpal's default is 8,192), and says which: the proactive guard reads it before
/// every request. Between the two, every request is refused ("larger than the context the model is
/// loaded with") while compaction waits for 80 % of a window that does not exist — so the conversation
/// is stuck until the user clears it, although compaction exists precisely for that.
/// </summary>
public class LoadedWindowCompactionTests
{
    private static InferpalConfig Config() => new()
    {
        ContextWindowSize        = 8_192,
        ContextWindowKeepTurns   = 2,
        CompactionEnabled        = false,   // truncation: no summarising call to script
        KvCacheAnchorMessages    = 0,
        CompactionTimeoutSeconds = 10,
    };

    private static List<ChatMessageDto> LongHistory()
    {
        var h = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 12; i++)
        {
            h.Add(new ChatMessageDto("user", $"question {i}"));
            h.Add(new ChatMessageDto("assistant", $"answer {i}"));
        }
        return h;
    }

    // 3,500 tokens: past 80 % of a 4,096 window, well under 80 % of the configured 8,192.
    private const int LastPrompt = 3_500;

    [Fact]
    public async Task AModelLoadedSmallerThanConfigured_IsCompactedAgainstTheLoadedWindow()
    {
        var client = new FakeInferenceProvider { LoadedContextWindow = 4_096 };

        var decision = await ContextManager.PrepareAsync(
            LongHistory(), Config(), client, LastPrompt, onStep: null, CancellationToken.None, model: "glm");

        Assert.Equal(["glm"], client.LoadedContextQueries);          // witness: the turn's model was asked
        Assert.NotEqual(ContextOutcome.None, decision.Outcome);
    }

    [Fact]
    public async Task AServerThatCannotSay_LeavesTheConfiguredWindow()
    {
        // Reference arm: Ollama loads with the configured window and reports nothing.
        var decision = await ContextManager.PrepareAsync(
            LongHistory(), Config(), new FakeInferenceProvider { LoadedContextWindow = null },
            LastPrompt, onStep: null, CancellationToken.None, model: "glm");

        Assert.Equal(ContextOutcome.None, decision.Outcome);
    }

    [Fact]
    public async Task AModelLoadedLargerThanConfigured_KeepsTheConfiguredWindow()
    {
        // The configured window is the user's own budget (it also sizes Ollama's num_ctx): a larger
        // loaded window never raises it.
        var decision = await ContextManager.PrepareAsync(
            LongHistory(), Config(), new FakeInferenceProvider { LoadedContextWindow = 65_536 },
            LastPrompt, onStep: null, CancellationToken.None, model: "glm");

        Assert.Equal(ContextOutcome.None, decision.Outcome);
    }

    // ── The gauges show the same window ───────────────────────────────────────

    [Theory]
    [InlineData(4_096,  4_096)]    // loaded smaller: that is the window
    [InlineData(null,   8_192)]    // reference arm: the server cannot say
    [InlineData(65_536, 8_192)]    // loaded larger: the configured budget stands
    public async Task TheDecision_CarriesTheWindowItMeasuredAgainst(int? loaded, int expected)
    {
        var decision = await ContextManager.PrepareAsync(
            LongHistory(), Config(), new FakeInferenceProvider { LoadedContextWindow = loaded },
            lastPromptTokens: 100, onStep: null, CancellationToken.None, model: "glm");

        Assert.Equal(expected, decision.Window);
    }

    /// <summary>
    /// ⚠ The context gauge read the CONFIGURED window in both front-ends: with a model loaded smaller,
    /// it showed room — "40 % of 8,192" — while every request was being refused.
    /// </summary>
    [Fact]
    public void TheVisualStudioGauge_ReadsTheWindowOfTheLastCheck()
    {
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.UiHelpers.cs"));
        var at   = code.IndexOf("ContextBudgetGauge.Compute(", StringComparison.Ordinal);
        Assert.True(at >= 0, "the gauge is gone");                                     // WITNESS
        var call = code[at..code.IndexOf(';', at)];

        Assert.DoesNotContain("_config.ContextWindowSize", call, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVsCodeGauge_TakesTheWindowFromTheTurn()
    {
        string Ts(params string[] parts) => SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts))));

        Assert.Contains("result.contextWindow", Ts("vscode", "src", "chatViewProvider.ts"), StringComparison.Ordinal);
        var webview = Ts("vscode", "src", "webview", "main.ts");
        var turnEnded = webview[webview.IndexOf("case 'turnEnded'", StringComparison.Ordinal)..];
        Assert.Contains("msg.contextWindow", turnEnded[..turnEnded.IndexOf("break;", StringComparison.Ordinal)],
                        StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs")]
    [InlineData("Inferpal.Host", "HostServer.cs")]
    public void BothFrontEnds_TellTheContextCheckWhichModelAnswers(params string[] parts)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));
        var at   = code.IndexOf("ContextManager.PrepareAsync(", StringComparison.Ordinal);
        Assert.True(at >= 0, "the context check is gone from this front-end");       // WITNESS
        var call = code[at..code.IndexOf(';', at)];

        Assert.Contains("model:", call, StringComparison.Ordinal);
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
    /// <summary>The window of the turn reaches the adapter, whose gauge shows it instead of the setting.</summary>
    [Fact]
    public async Task ChatSend_ReturnsTheWindowTheTurnWasMeasuredAgainst()
    {
        using var h = CreateHarness(cfg => cfg.ContextWindowSize = 8_192);
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.LoadedContextWindow = 4_096;
        h.Fake.ChatResult = new ChatTurnResult("ok", null, 0, 0);

        var result = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "hi", agentMode = false }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(4_096, result.ContextWindow);
    }
}
