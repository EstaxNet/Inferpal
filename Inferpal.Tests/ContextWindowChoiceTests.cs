using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The first run, <c>/setup</c> and "Suggest" size the chat model's context window for the card on Ollama: the largest the
/// card holds beside the models that live with it, at most what the battery measures, never below the factory window —
/// and only while the window is the product's (the factory one, or the one it last wrote), in this window or the other
/// editor's.
/// </summary>
/// <remarks>
/// At the factory 8 192 tokens, the tool definitions alone take more than half the window: on the default path a five
/// command task ended on "the request is larger than the context the model is loaded with", while the size that fits the
/// card was computed for <c>/hardware</c> only. A window the product wrote is resized when the chat model changes: kept
/// as if chosen, the window sized for an 8B model stayed on the 12B that replaced it, past the card's memory.
/// </remarks>
[Collection(GlobalConfigPathCollection.Name)]
public sealed class ContextWindowChoiceTests : IDisposable
{
    private const long Gb = 1L << 30;

    /// <summary>A Llama-shaped model: 32 layers × 8 KV heads × (128 + 128) × 2 bytes = 128 KiB of cache per token.</summary>
    private static readonly ModelArchInfo Llama = new(32, 32, 8, 4096, 131072);

    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _path = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"window-{Guid.NewGuid():N}.json");

    public ContextWindowChoiceTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { File.Delete(_path); } catch { }
    }

    private static int Expected(double usableGb, long weights) =>
        Math.Min(ModelCatalog.MaxSafeNumCtx(usableGb, weights, Llama), ContextWindowChoice.MeasuredCeiling);

    [Fact]
    public void TheWindow_IsWhatTheCardHolds_LessWhatLivesBeside()
    {
        var alone  = ContextWindowChoice.Fits(8, 5 * Gb, Llama, besideBytes: 0);
        var beside = ContextWindowChoice.Fits(8, 5 * Gb, Llama, besideBytes: Gb);

        Assert.Equal(Expected(8, 5 * Gb), alone);
        Assert.Equal(Expected(7, 5 * Gb), beside);
        Assert.True(beside < alone && beside > ContextWindowChoice.Factory);
    }

    [Fact]
    public void ABigCard_StopsAtWhatTheBatteryMeasures_ButWhatTheMemoryHoldsIsNotCapped()
    {
        Assert.Equal(ContextWindowChoice.MeasuredCeiling, ContextWindowChoice.Fits(24, 5 * Gb, Llama, besideBytes: 0));
        // /hardware's figure is a fact about the memory: the measured ceiling is not a VRAM limit.
        Assert.Equal(ModelCatalog.MaxSafeNumCtx(24, 5 * Gb, Llama), ContextWindowChoice.FitsMemory(24, 5 * Gb, Llama, besideBytes: 0));
        Assert.True(ContextWindowChoice.FitsMemory(24, 5 * Gb, Llama, besideBytes: 0) > ContextWindowChoice.MeasuredCeiling);
    }

    [Fact]
    public void AnUnknownCard_OrWeightsThatDoNotFit_HoldNoWindow()
    {
        Assert.Equal(0, ContextWindowChoice.Fits(4, 5 * Gb, Llama, besideBytes: 0));     // the weights alone do not fit
        Assert.Equal(0, ContextWindowChoice.Fits(0, 5 * Gb, Llama, besideBytes: 0));     // unknown card
        Assert.Equal(0, ContextWindowChoice.Fits(8, 0, Llama, besideBytes: 0));          // unknown weights
    }

    private static FakeInferenceProvider Server() => new()
    {
        Installed =
        [
            new InstalledModelInfo("granite4.2:8b", 5 * Gb, ["completion", "tools"]),
            new InstalledModelInfo("big:12b", 12 * Gb, ["completion", "tools"]),
            new InstalledModelInfo("bge-m3:latest", Gb, ["embedding"]),
        ],
        OnShow = m => m is "granite4.2:8b" or "big:12b" ? Llama : null,
    };

    private static Task<WindowAdvice> Advise(double card, IReadOnlyList<string>? beside = null, string provider = "ollama",
                                             string model = "granite4.2:8b")
    {
        var server = Server();
        return ContextWindowChoice.AdviseAsync(server, provider, model, beside ?? [], server.Installed, card, CancellationToken.None);
    }

    [Fact]
    public async Task OnOllama_TheWindowIsSaid_WithTheCard_AndCountsWhatLivesBeside()
    {
        // 9 GB: between the smallest window worth writing and the ceiling, where the model beside it changes the figure.
        var advice = await Advise(9, ["bge-m3:latest"]);

        var expected = Expected(9 - ModelCatalog.EstimateVramBytes(Gb) / ModelCatalog.BytesPerGb, 5 * Gb);
        Assert.Equal(expected, advice.Tokens);
        Assert.NotEqual(Expected(9, 5 * Gb), advice.Tokens);   // the search model beside it is counted
        Assert.InRange(expected, ContextWindowChoice.SmallestWorthwhile, ContextWindowChoice.MeasuredCeiling - 1);
        Assert.Equal(Strings.WindowChosen(expected, "9"), advice.Sentence);
    }

    /// <summary>A window between the factory one and <see cref="ContextWindowChoice.SmallestWorthwhile"/> does not change a
    /// turn's outcome: the factory window stays, unsaid — as for a model that does not fit at all.</summary>
    [Fact]
    public async Task ATooSmallGain_OrAModelThatDoesNotFit_KeepsTheFactoryWindow_Unsaid()
    {
        var small = ContextWindowChoice.Fits(8, 5 * Gb, Llama, ModelCatalog.EstimateVramBytes(Gb));
        Assert.InRange(small, ContextWindowChoice.Factory + 1, ContextWindowChoice.SmallestWorthwhile - 1);   // witness

        Assert.Equal(new WindowAdvice(ContextWindowChoice.Factory, null), await Advise(8, ["bge-m3:latest"]));
        Assert.Equal(new WindowAdvice(ContextWindowChoice.Factory, null), await Advise(4));
    }

    /// <summary>Models that do not fit the card by the estimate its fit line uses get no window "sized for the card": the
    /// raw weights leave room for a cache, the estimate says the models already overflow.</summary>
    [Fact]
    public async Task ModelsThatDoNotFitTheCard_GetNoWindowSizedForIt()
    {
        Assert.True(ContextWindowChoice.Fits(14.3, 12 * Gb, Llama, 0) >= ContextWindowChoice.SmallestWorthwhile);     // witness
        Assert.True(ModelCatalog.EstimateVramBytes(12 * Gb) > 14.3 * Gb);                                           // witness

        Assert.Equal(new WindowAdvice(ContextWindowChoice.Factory, null), await Advise(14.3, model: "big:12b"));
    }

    [Fact]
    public async Task AnUnknownCard_IsSaid_WithItsRemedy_AndDecidesNothing()
    {
        var advice = await Advise(0);

        Assert.Null(advice.Tokens);
        Assert.Contains("/hardware", advice.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherServerType_OrAModelWithoutAShape_IsLeftAlone()
    {
        Assert.Equal(new WindowAdvice(null, null), await Advise(12, provider: "lmstudio"));
        Assert.Equal(new WindowAdvice(null, null), await Advise(12, model: "unknown-model:1b"));
    }

    [Fact]
    public void Beside_FollowsTheSwitches_AndTheChatsOwnModelIsNotASecondModel()
    {
        var on = new InferpalConfig { RagEnabled = true, InlineCompletionEnabled = true };
        Assert.Equal(["bge-m3", "qwen2.5-coder:7b"], ContextWindowChoice.Beside(on, "granite4.2:8b", "bge-m3", "qwen2.5-coder:7b"));
        Assert.Equal(["bge-m3"], ContextWindowChoice.Beside(on, "granite4.2:8b", "bge-m3", "Granite4.2:8B"));
        Assert.Equal(["bge-m3"], ContextWindowChoice.Beside(on, "granite4.2:8b", "bge-m3", null));

        var off = new InferpalConfig { RagEnabled = false, InlineCompletionEnabled = false };
        Assert.Empty(ContextWindowChoice.Beside(off, "granite4.2:8b", "bge-m3", "qwen2.5-coder:7b"));

        var elsewhere = new InferpalConfig
        {
            RagEmbeddingRunsOn = RoleServers.OnOther, RagEmbeddingBaseUrl = "http://192.0.2.7:11434",
            InlineCompletionRunsOn = RoleServers.OnOther, InlineCompletionBaseUrl = "http://192.0.2.7:11434",
        };
        Assert.Empty(ContextWindowChoice.Beside(elsewhere, "granite4.2:8b", "bge-m3", "qwen2.5-coder:7b"));
    }

    [Fact]
    public void Beside_FromTheSettings_ResolvesTheAutomaticSearchModel_AndKeepsAChosenCompletionModel()
    {
        var installed = Server().Installed;
        var automatic = new InferpalConfig { DefaultModel = "granite4.2:8b", RagEmbeddingModel = "" };
        Assert.Equal(["bge-m3:latest"], ContextWindowChoice.Beside(automatic, "granite4.2:8b", installed));

        var withCoder = new InferpalConfig { DefaultModel = "granite4.2:8b", InlineCompletionModel = "qwen2.5-coder:7b" };
        Assert.Equal(["bge-m3:latest", "qwen2.5-coder:7b"], ContextWindowChoice.Beside(withCoder, "granite4.2:8b", installed));
    }

    [Fact]
    public void TheProductsWindow_IsResized_AndAChosenOne_NeverIs()
    {
        var factory = new InferpalConfig();
        Assert.True(ContextWindowChoice.Apply(factory, 24576));
        Assert.Equal((24576, 24576), (factory.ContextWindowSize, factory.ContextWindowSizeAuto));

        // Written by the product for another model: resized, back to the factory window when nothing larger fits.
        Assert.True(ContextWindowChoice.Apply(factory, ContextWindowChoice.Factory));
        Assert.Equal((ContextWindowChoice.Factory, 0), (factory.ContextWindowSize, factory.ContextWindowSizeAuto));

        var chosen = new InferpalConfig { ContextWindowSize = 16384 };
        Assert.False(ContextWindowChoice.Apply(chosen, 24576));
        Assert.Equal(16384, chosen.ContextWindowSize);

        // Changed by hand after the product wrote it: the person's now.
        var edited = new InferpalConfig { ContextWindowSize = 30720, ContextWindowSizeAuto = 24576 };
        Assert.False(ContextWindowChoice.Apply(edited, 12288));
        Assert.Equal(30720, edited.ContextWindowSize);
    }

    private void WriteAsTheOtherEditor(Action<InferpalConfig> change)
    {
        var other = JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;
        change(other);
        File.WriteAllText(_path, JsonSerializer.Serialize(other));
    }

    [Fact]
    public void AWindowChosenInTheOtherEditor_IsReadInTheFile_AndStands()
    {
        new InferpalConfig().Save();
        var mine = InferpalConfig.Load();
        WriteAsTheOtherEditor(c => c.ContextWindowSize = 16384);

        Assert.False(ContextWindowChoice.Apply(mine, 24576));
        mine.Save();   // nothing of this copy's goes over it
        Assert.Equal(16384, InferpalConfig.LoadOwnCopy().ContextWindowSize);
    }

    /// <summary>The copy holds the product's window, the FILE a window the person set since in the other editor: judged
    /// on the copy, it was resized over.</summary>
    [Fact]
    public void AWindowSetInTheOtherEditor_OverTheProductsOne_IsReadInTheFile_AndStands()
    {
        new InferpalConfig { ContextWindowSize = 16384, ContextWindowSizeAuto = 16384 }.Save();
        var mine = InferpalConfig.Load();
        Assert.True(ContextWindowChoice.IsAutomatic(mine));                                                    // witness
        WriteAsTheOtherEditor(c => c.ContextWindowSize = 12000);

        Assert.False(ContextWindowChoice.Apply(mine, 24576));
        Assert.Equal(12000, InferpalConfig.LoadOwnCopy().ContextWindowSize);
    }

    [Fact]
    public void AWindowTheOtherEditorSized_IsStillTheProducts_AndIsResized()
    {
        new InferpalConfig().Save();
        var mine = InferpalConfig.Load();
        WriteAsTheOtherEditor(c => { c.ContextWindowSize = 24576; c.ContextWindowSizeAuto = 24576; });

        Assert.True(ContextWindowChoice.Apply(mine, 12288));
        Assert.Equal((12288, 12288), (mine.ContextWindowSize, mine.ContextWindowSizeAuto));
    }

    /// <summary>Visual Studio's first run and <c>/setup</c> do not run headless: their source is read. The card's memory is
    /// detected BEFORE the window is sized, only Ollama is listed, only an automatic window is written, and a size is
    /// said only once written.</summary>
    [Fact]
    public void VisualStudiosFirstRun_SizesTheWindowAfterTheCard_AndSaysWhatItWrote()
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(),
                                                                 "Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs"));
        var start = code.IndexOf("private async Task RunSetupDiscoveryAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "RunSetupDiscoveryAsync not found");   // WITNESS
        var body = code[start..code.IndexOf("private async Task HandleSetupCommandAsync", start, StringComparison.Ordinal)];

        var budget = body.IndexOf("HardwareProfile.EnsureBudgetAsync(_config", StringComparison.Ordinal);
        var advise = body.IndexOf("ContextWindowChoice.AdviseAsync(", StringComparison.Ordinal);
        Assert.True(budget >= 0 && advise > budget, "the window is sized before the card's memory is known");
        Assert.Contains("ContextWindowChoice.Applies(_config.Provider)", body, StringComparison.Ordinal);
        Assert.Contains("ContextWindowChoice.IsAutomatic(_config)", body, StringComparison.Ordinal);
        Assert.Contains("ContextWindowChoice.Beside(_config, best, installed)", body, StringComparison.Ordinal);
        Assert.Contains("ContextWindowChoice.Apply(_config, tokens)", body, StringComparison.Ordinal);
        Assert.Contains("window.Tokens is null || windowWritten", body, StringComparison.Ordinal);
    }
}
