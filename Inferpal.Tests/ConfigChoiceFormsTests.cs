using System.IO;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A drop-down value written in a form the product understands (by hand, through the JSON editor, or by
/// copying the documentation) is read as the option it names — by the product AND by both panels.
/// </summary>
/// <remarks>
/// The panels compared the saved value to their options' codes letter for letter. <c>"LMStudio"</c>, which the
/// factory serves as LM Studio, showed as "Ollama" in both settings windows, and the first Save — for any
/// other setting — wrote <c>ollama</c>: the backend changed silently.
/// </remarks>
[Collection(GlobalConfigPathCollection.Name)]
public class ConfigChoiceFormsTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _dir      =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"choice-forms-{Guid.NewGuid():N}");
    private readonly string  _path;

    public ConfigChoiceFormsTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.json");
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("LMStudio", "lmstudio")]
    [InlineData(" Ollama ", "ollama")]
    [InlineData("openai", "openai-compatible")]
    [InlineData("llamacpp", "llamacpp")]   // unknown: kept as is (the factory traces it and falls back)
    public void AProviderForm_IsReadAsTheCodeOfItsOption(string written, string expected)
    {
        var cfg = new InferpalConfig { Provider = written };
        InferpalConfig.CanonicalizeChoices(cfg);
        Assert.Equal(expected, cfg.Provider);
    }

    [Theory]
    [InlineData("fr-FR", "fr")]
    [InlineData("FR", "fr")]
    [InlineData("zh-cn", "zh-CN")]
    [InlineData("en-US", "en")]
    [InlineData("", "")]             // Auto
    [InlineData("pt-BR", "pt-BR")]   // no offered language: kept
    [InlineData("zh-TW", "zh-TW")]   // traditional Chinese is not zh-CN
    public void ALanguageForm_IsReadAsTheLanguageItNames(string written, string expected)
    {
        var cfg = new InferpalConfig { Language = written };
        InferpalConfig.CanonicalizeChoices(cfg);
        Assert.Equal(expected, cfg.Language);
    }

    [Theory]
    [InlineData("fast", "Fast")]
    [InlineData("highaccuracy", "HighAccuracy")]
    [InlineData("Turbo", "Turbo")]   // unknown: kept
    public void AnInlineModeForm_IsReadAsThePresetItNames(string written, string expected)
    {
        var cfg = new InferpalConfig { InlineCompletionMode = written };
        InferpalConfig.CanonicalizeChoices(cfg);
        Assert.Equal(expected, cfg.InlineCompletionMode);
    }

    // Loading goes through the canonicalization: that is what both panels read (config/get serializes the
    // loaded instance). No language here: Load applies it to the whole process.
    [Fact]
    public void Load_ReadsAnEquivalentForm_AsTheOptionThePanelsList()
    {
        File.WriteAllText(_path, """{ "provider": "LMStudio", "inlineCompletionMode": "fast" }""");

        var loaded = InferpalConfig.Load();

        Assert.Equal("lmstudio", loaded.Provider);
        Assert.Equal("Fast", loaded.InlineCompletionMode);
    }

    // The published documentation gave `openai`; those who copied it were talking to Ollama.
    [Fact]
    public void TheDocumentedOpenAiValue_BuildsTheOpenAiCompatibleClient()
    {
        var provider = InferenceProviderFactory.Create(new InferpalConfig { Provider = "openai" });

        Assert.IsType<OpenAiCompatibleClient>(provider);
        Assert.Equal("OpenAI-compatible", InferenceProviderFactory.DisplayName("openai"));
    }

    // Reference arm: a truly unknown code still falls back to Ollama.
    [Fact]
    public void AnUnknownProvider_StillFallsBackToOllama()
    {
        Assert.IsType<OllamaClient>(InferenceProviderFactory.Create(new InferpalConfig { Provider = "llamacpp" }));
    }

    [Theory]
    [InlineData("fast", "Fast")]
    [InlineData("HIGHACCURACY", "HighAccuracy")]
    public void AnInlineCompletionMode_IsReadWhateverItsCase(string written, string preset)
    {
        Assert.Equal(FimContextBuilder.GetSettings(preset), FimContextBuilder.GetSettings(written));
        // Witness: the presets really are distinct, otherwise the equality proves nothing.
        Assert.NotEqual(FimContextBuilder.GetSettings("Default"), FimContextBuilder.GetSettings(preset));
    }
}
