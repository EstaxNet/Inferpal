using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Nothing is loaded" is said only by a server that said so.
/// </summary>
/// <remarks>
/// The loaded list reads empty on any failure. LM Studio keeps its loaded state on its NATIVE surface only, and a
/// reverse proxy that routes just <c>/v1</c> — the common shape of an LM Studio exposed on a domain — answers the model
/// list (the client falls back to <c>/v1/models</c>) and nothing else: <c>/hardware</c>, <c>/models running</c> and the
/// settings page answered "nothing is loaded" over loaded models. The real client is measured here against a loopback
/// server shaped like that proxy.
/// </remarks>
public class LoadedModelsNotSaidTests
{
    /// <summary>LM Studio behind a proxy that routes only /v1: the model list answers, the native surface is 404.</summary>
    private static LoopbackHttpServer V1OnlyProxy() => new(path =>
        path.StartsWith("/v1/models", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(new { data = new[] { new { id = "qwen/qwen3.8-27b" } } })
            : null);

    private static InferpalConfig LmStudio(string url) =>
        new() { Provider = "lmstudio", BaseUrl = url, DefaultModel = "qwen/qwen3.8-27b", VramBudgetGb = 24 };

    [Fact]
    public async Task TheRealClient_BehindAV1OnlyProxy_SaysItDoesNotKnowWhatIsLoaded()
    {
        using var server = V1OnlyProxy();
        var client = new LmStudioClient(LmStudio(server.BaseUrl));

        Assert.Single(await client.ListInstalledModelsAsync(CancellationToken.None));   // WITNESS: the server answers
        Assert.Null(await client.ReadRunningModelsAsync(CancellationToken.None));
        Assert.Empty(await client.GetRunningModelsAsync(CancellationToken.None));       // the old reading, kept for its readers
    }

    [Fact]
    public async Task Hardware_BehindAV1OnlyProxy_SaysUnknown_NotNone()
    {
        using var server = V1OnlyProxy();
        var config = LmStudio(server.BaseUrl);
        var client = new LmStudioClient(config);

        var report = (await HardwareCommandHandler.HandleAsync(config, client, ["/hardware"], CancellationToken.None)).Message;

        Assert.Contains("qwen/qwen3.8-27b", report, StringComparison.Ordinal);   // WITNESS: the installed list was read
        Assert.Contains(Strings.HardwareLoadedNotSaid, report, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.HardwareLoadedNone, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hardware_WithNothingLoaded_StillSaysNone()
    {
        // Reference arm: a server that answered an empty loaded list has said that nothing is loaded.
        var fake   = new FakeInferenceProvider { Installed = [new("m", 1)] };
        var report = (await HardwareCommandHandler.HandleAsync(
            new InferpalConfig { VramBudgetGb = 24 }, fake, ["/hardware"], CancellationToken.None)).Message;

        Assert.Contains(Strings.HardwareLoadedNone, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsRunning_WhenTheLoadedListIsNotSaid_SaysSo()
    {
        var fake = new FakeInferenceProvider { RunningNotSaid = true, Installed = [new("m", 1)] };

        var message = (await ModelsCommandHandler.HandleAsync(fake, new InferpalConfig(), ["/models", "running"],
                                                              CancellationToken.None)).Message;

        Assert.Equal(Strings.LoadedModelsUnknown, message);
    }

    [Fact]
    public async Task ModelsRunning_OnAnUnreachableServer_StillSaysUnreachable()
    {
        // Reference arm: nobody said anything because nobody answered — the existing sentence.
        var fake = new FakeInferenceProvider { RunningNotSaid = true, ConnectionOk = false };

        var message = (await ModelsCommandHandler.HandleAsync(fake, new InferpalConfig(), ["/models", "running"],
                                                              CancellationToken.None)).Message;

        Assert.NotEqual(Strings.LoadedModelsUnknown, message);
        Assert.NotEqual(Strings.ModelsNoneRunning, message);
    }

    [Theory]
    [InlineData(true,  true,  "unknown")]
    [InlineData(false, true,  "none")]
    [InlineData(true,  false, "unreachable")]
    public async Task TheSettingsCard_SaysWhatTheServerSaid(bool notSaid, bool reachable, string expected)
    {
        var fake = new FakeInferenceProvider { RunningNotSaid = notSaid, ConnectionOk = reachable };

        var card = await LoadedModelsCard.ReadAsync(fake, new InferpalConfig(), null, CancellationToken.None);

        Assert.Equal(expected switch
        {
            "unknown"     => Strings.LoadedModelsUnknown,
            "none"        => Strings.LoadedModelsNone,
            _             => Strings.LoadedModelsUnreachable,
        }, card.Summary);
    }
}
