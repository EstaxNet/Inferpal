using System.IO;
using Inferpal.Config;
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
