using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Bench;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Two sizes of one family are two models. The comparison shared by the router, the arena and the bench
/// ignored everything after <c>:</c>, so <c>qwen3:8b</c> and <c>qwen3:32b</c> were "the same model" —
/// only Ollama's implicit <c>:latest</c> tag may be ignored.
/// </summary>
public class ModelNameComparisonTests
{
    [Theory]
    [InlineData("llama3.1",     "llama3.1:latest", true)]
    [InlineData("qwen3:latest", "qwen3",           true)]
    [InlineData("QWEN3:8B",     "qwen3:8b",        true)]
    [InlineData("qwen3:8b",     "qwen3:32b",       false)]
    [InlineData("qwen3",        "qwen3:8b",        false)]
    public void SameModelName_IgnoresOnlyTheImplicitLatestTag(string a, string b, bool same) =>
        Assert.Equal(same, ModelCatalog.SameModelName(a, b));

    /// <summary>
    /// The utility model counts as warm only when THAT model is loaded: a loaded <c>qwen3:32b</c> made a
    /// recommended <c>qwen3:1.7b</c> look warm, so auto mode routed titles and commit messages to a cold
    /// model — the VRAM swap it exists to avoid.
    /// </summary>
    [Fact]
    public void AnotherSizeOfTheRecommendedModel_IsNotWarm()
    {
        var cfg = new InferpalConfig { DefaultModel = "chat-model", ModelRouterAuto = true };

        Assert.Equal("chat-model", ModelRouter.ResolveUtility(cfg, "qwen3:1.7b", ["qwen3:32b"]));

        // Witness: the implicit tag still matches.
        Assert.Equal("qwen3", ModelRouter.ResolveUtility(cfg, "qwen3", ["qwen3:latest"]));
    }

    /// <summary>The bench never credits the VRAM of another size of the benched model.</summary>
    [Fact]
    public async Task Bench_DoesNotCreditAnotherSizesVram()
    {
        var fake = new FakeInferenceProvider
        {
            OnFim         = (_, _) => "",
            Running       = [new RunningModelInfo("qwen3:32b", 20_000_000_000, "")],
            OnChatRequest = (model, messages, tools, onToken) => Task.FromResult(new ChatTurnResult("OK", null, 10, 5)),
        };

        var result = await BenchRunner.RunModelAsync(fake, "qwen3:8b", CancellationToken.None);

        Assert.Equal(-1, result.VramBytes);
    }
}
