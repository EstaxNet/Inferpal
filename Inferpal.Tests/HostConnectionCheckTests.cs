using Inferpal.Host;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// `connection/check` — the VS Code Test button: a server that answers comes back with the line to show, its kind and
/// the models it lists, read at the address in the form.
/// </summary>
public partial class HostServerTests
{
    [Fact]
    public async Task ConnectionCheck_AServerThatAnswers_SaysHowManyModelsItLists()
    {
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/models", StringComparison.Ordinal)
                ? "{\"data\":[{\"id\":\"qwen3.8-27b\"},{\"id\":\"devstral-small-2\"},{\"id\":\"text-embedding-qwen3\"}]}"
                : "not found",
            path => path.StartsWith("/v1/models", StringComparison.Ordinal) ? 200 : 404);
        using var h = CreateHarness();
        await h.InitializeAsync();

        var result = await Call<ConnectionCheckResult>(h, "connection/check", new { baseUrl = server.BaseUrl });

        Assert.True(result.Ok);
        Assert.Equal(
            Services.Presentation.ConnectionStatusPresenter.Answered(
                Services.Inference.InferenceProviderFactory.DisplayName(result.Provider!), 3),
            result.Answered);
    }

    [Fact]
    public async Task ConnectionCheck_NothingAnswers_CarriesNoAnsweredLine()
    {
        // Reference arm: an address nothing listens on answers no line of its own.
        using var h = CreateHarness();
        await h.InitializeAsync();

        var result = await Call<ConnectionCheckResult>(h, "connection/check", new { baseUrl = "http://127.0.0.1:9" });

        Assert.False(result.Ok);
        Assert.Null(result.Answered);
    }
}
