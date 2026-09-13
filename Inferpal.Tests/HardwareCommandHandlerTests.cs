using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

public class HardwareCommandHandlerTests
{
    private static string[] Cmd(params string[] args) => ["/hardware", .. args];

    // A config with a budget already set, so EnsureBudgetAsync is a no-op (no nvidia-smi probe in tests).
    private static InferpalConfig ConfigWithBudget(double gb) => new() { VramBudgetGb = gb, DefaultModel = "llama3.1" };

    [Fact]
    public async Task SetBudget_PositiveValue_RequestsSetAndConfirms()
    {
        var result = await HardwareCommandHandler.HandleAsync(
            new InferpalConfig(), new FakeInferenceProvider(), Cmd("12"), CancellationToken.None);

        Assert.Equal(12, result.SetBudgetGb);
        Assert.Equal(Strings.HardwareBudgetSet("12"), result.Message);
    }

    [Fact]
    public async Task SetBudget_DecimalValue_ParsedInvariant()
    {
        var result = await HardwareCommandHandler.HandleAsync(
            new InferpalConfig(), new FakeInferenceProvider(), Cmd("7.5"), CancellationToken.None);

        Assert.Equal(7.5, result.SetBudgetGb);
    }

    [Fact]
    public async Task SetBudget_ZeroOrNegative_ReturnsUsageNoSet()
    {
        var zero = await HardwareCommandHandler.HandleAsync(
            new InferpalConfig(), new FakeInferenceProvider(), Cmd("0"), CancellationToken.None);
        var neg = await HardwareCommandHandler.HandleAsync(
            new InferpalConfig(), new FakeInferenceProvider(), Cmd("-3"), CancellationToken.None);

        Assert.Equal(Strings.HardwareUsage, zero.Message);
        Assert.Null(zero.SetBudgetGb);
        Assert.Equal(Strings.HardwareUsage, neg.Message);
        Assert.Null(neg.SetBudgetGb);
    }

    [Fact]
    public async Task Report_BackendWithoutVramMonitoring_ReturnsNoVramNotice()
    {
        var client = new FakeInferenceProvider { Capabilities = ProviderCapabilities.OpenAiCompatible };

        var result = await HardwareCommandHandler.HandleAsync(
            ConfigWithBudget(8), client, Cmd(), CancellationToken.None);

        Assert.Equal(Strings.HardwareNoVramBackend, result.Message);
        Assert.Null(result.SetBudgetGb);
    }

    [Fact]
    public async Task Report_WithVramMonitoring_RendersProfileReport()
    {
        var client = new FakeInferenceProvider
        {
            Capabilities = ProviderCapabilities.Ollama,
            ModelNames   = ["llama3.1"],
            Installed    = [new InstalledModelInfo("llama3.1", 5L * 1024 * 1024 * 1024)],
            Running      = [new RunningModelInfo("llama3.1", 5L * 1024 * 1024 * 1024, "")],
            OnShow       = _ => new ModelArchInfo(32, 32, 8, 4096, 8192),
        };

        var result = await HardwareCommandHandler.HandleAsync(
            ConfigWithBudget(12), client, Cmd(), CancellationToken.None);

        Assert.Null(result.SetBudgetGb);
        Assert.Contains(Strings.HardwareReportHeading, result.Message);
    }

    /// <summary>
    /// The configured name and the installed one differ only by Ollama's implicit tag (<c>qwen3</c> vs
    /// <c>qwen3:latest</c>): the weights still count, or the recommended <c>num_ctx</c> is computed over
    /// the whole budget and advises a context whose KV cache does not fit.
    /// </summary>
    [Fact]
    public async Task Report_CountsTheWeights_WhenTheConfiguredNameOmitsTheLatestTag()
    {
        const long gb = 1024L * 1024 * 1024;
        var arch   = new ModelArchInfo(32, 32, 8, 4096, 1_000_000);
        var client = new FakeInferenceProvider
        {
            Capabilities = ProviderCapabilities.Ollama,
            Installed    = [new InstalledModelInfo("qwen3:latest", 6 * gb)],
            OnShow       = _ => arch,
        };
        var config = new InferpalConfig { VramBudgetGb = 8, DefaultModel = "qwen3" };

        var result = await HardwareCommandHandler.HandleAsync(config, client, Cmd(), CancellationToken.None);

        var withWeights    = Inferpal.Services.Inference.ModelCatalog.MaxSafeNumCtx(8, 6 * gb, arch);
        var withoutWeights = Inferpal.Services.Inference.ModelCatalog.MaxSafeNumCtx(8, 0, arch);
        Assert.NotEqual(withWeights, withoutWeights);   // witness: the case discriminates
        Assert.Contains(Strings.HardwareRecommendedCtx("qwen3", withWeights, ""), result.Message);
        Assert.DoesNotContain(Strings.HardwareRecommendedCtx("qwen3", withoutWeights, ""), result.Message);
    }
}
