using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the client-side Fill-in-the-Middle prompt builder used by the LM Studio provider
// (LM Studio's /v1/completions has no server-side suffix, so the FIM tokens are templated here).
public class FimTemplateTests
{
    [Theory]
    [InlineData("qwen2.5-coder-7b-instruct")]
    [InlineData("codegemma-7b")]
    public void Qwen_And_CodeGemma_UsePipeFimTokens(string model)
    {
        var spec = FimTemplate.Build(model, "PRE", "SUF");
        Assert.True(spec.IsFim);
        Assert.Equal("<|fim_prefix|>PRE<|fim_suffix|>SUF<|fim_middle|>", spec.Prompt);
    }

    [Fact]
    public void StarCoder_UsesBareFimTokens()
    {
        var spec = FimTemplate.Build("starcoder2-3b", "PRE", "SUF");
        Assert.True(spec.IsFim);
        Assert.Equal("<fim_prefix>PRE<fim_suffix>SUF<fim_middle>", spec.Prompt);
    }

    [Fact]
    public void CodeLlama_UsesPreSufMid()
    {
        var spec = FimTemplate.Build("codellama-13b", "PRE", "SUF");
        Assert.True(spec.IsFim);
        Assert.Equal("<PRE> PRE <SUF>SUF <MID>", spec.Prompt);
    }

    [Fact]
    public void DeepSeek_UsesDeepSeekTokens()
    {
        var spec = FimTemplate.Build("deepseek-coder-6.7b", "PRE", "SUF");
        Assert.True(spec.IsFim);
        Assert.Equal("<｜fim▁begin｜>PRE<｜fim▁hole｜>SUF<｜fim▁end｜>", spec.Prompt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some-unknown-model")]
    public void UnknownFamily_FallsBackToPrefixOnly(string? model)
    {
        var spec = FimTemplate.Build(model, "PREFIX", "SUFFIX");
        Assert.False(spec.IsFim);
        Assert.Equal("PREFIX", spec.Prompt);   // suffix dropped — completion still works
    }

    [Fact]
    public void Detection_IsCaseInsensitive()
        => Assert.True(FimTemplate.Build("Qwen2.5-Coder", "a", "b").IsFim);
}
