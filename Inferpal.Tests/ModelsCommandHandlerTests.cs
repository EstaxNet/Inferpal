using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

public class ModelsCommandHandlerTests
{
    private static string[] Cmd(params string[] args) => ["/models", .. args];

    private static RunningModelInfo Run(string name, long vramBytes) => new(name, vramBytes, "");

    [Fact]
    public async Task Delete_BackendWithoutModelManagement_ReturnsUnsupported()
    {
        var client = new FakeInferenceProvider { Capabilities = ProviderCapabilities.OpenAiCompatible };

        var result = await ModelsCommandHandler.HandleAsync(client, Cmd("delete", "foo"), CancellationToken.None);

        Assert.Equal(Strings.ModelsBackendUnsupported, result.Message);
        Assert.Empty(client.Deleted); // never reached the backend
    }

    [Fact]
    public async Task Running_BackendWithoutVramMonitoring_ReturnsUnsupported()
    {
        var client = new FakeInferenceProvider { Capabilities = ProviderCapabilities.OpenAiCompatible };

        var result = await ModelsCommandHandler.HandleAsync(client, Cmd("running"), CancellationToken.None);

        Assert.Equal(Strings.ModelsBackendUnsupported, result.Message);
    }

    [Fact]
    public async Task Delete_NoArg_ReturnsUsage()
    {
        var client = new FakeInferenceProvider();

        var result = await ModelsCommandHandler.HandleAsync(client, Cmd("delete"), CancellationToken.None);

        Assert.Equal(Strings.ModelsDeleteUsage, result.Message);
        Assert.Empty(client.Deleted);
    }

    [Fact]
    public async Task Delete_Success_CallsBackendAndConfirms()
    {
        var client = new FakeInferenceProvider { OnDelete = _ => true };

        var result = await ModelsCommandHandler.HandleAsync(client, Cmd("delete", "llama3.1"), CancellationToken.None);

        Assert.Equal(Strings.ModelsDeleted("llama3.1"), result.Message);
        Assert.Equal("llama3.1", Assert.Single(client.Deleted));
    }

    [Fact]
    public async Task Delete_Failure_ReportsFailure()
    {
        var client = new FakeInferenceProvider { OnDelete = _ => false };

        var result = await ModelsCommandHandler.HandleAsync(client, Cmd("delete", "ghost"), CancellationToken.None);

        Assert.Equal(Strings.ModelsDeleteFailed("ghost"), result.Message);
    }

    [Fact]
    public async Task Running_None_ReturnsEmptyNotice()
    {
        var client = new FakeInferenceProvider { Running = [] };

        var result = await ModelsCommandHandler.HandleAsync(client, Cmd("running"), CancellationToken.None);

        Assert.Equal(Strings.ModelsNoneRunning, result.Message);
    }

    [Fact]
    public async Task Running_WithModels_ListsThem()
    {
        var client = new FakeInferenceProvider { Running = [Run("qwen3", 5L * 1024 * 1024 * 1024)] };

        var result = await ModelsCommandHandler.HandleAsync(client, Cmd("running"), CancellationToken.None);

        Assert.Contains("qwen3", result.Message);
    }

    [Fact]
    public async Task List_None_ReturnsHint()
    {
        var client = new FakeInferenceProvider { ModelNames = [] };

        var result = await ModelsCommandHandler.HandleAsync(client, Cmd(), CancellationToken.None);

        Assert.Equal(Strings.ModelsNoneInstalled, result.Message);
    }

    [Fact]
    public async Task List_WithModels_RendersTableWithLoadedMarker()
    {
        var client = new FakeInferenceProvider
        {
            ModelNames = ["llama3.1", "qwen3"],
            Running    = [Run("qwen3", 4L * 1024 * 1024 * 1024)],
        };

        var result = await ModelsCommandHandler.HandleAsync(client, Cmd("list"), CancellationToken.None);

        Assert.Contains("llama3.1", result.Message);
        Assert.Contains("qwen3", result.Message);
        Assert.Contains("🟢", result.Message); // qwen3 is loaded
    }
}
