using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A server that ANSWERS the connection check with a refusal — 401 for a missing or wrong API key, 404 for a URL that
/// points at the wrong path — is running. The badge said "Cannot reach … Start it … verify the URL … check the firewall",
/// four remedies for a server that is up; and each check counted against the chat's breaker, so after five of them the
/// chat said "circuit breaker open" instead of the server's own "invalid API key".
/// </summary>
public class ConnectionRefusedTests
{
    private static OpenAiCompatibleClient OpenAi(LoopbackHttpServer server) =>
        new(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl, ApiKey = "wrong" });

    private static LoopbackHttpServer KeyRefused() => new(
        path => path.StartsWith("/v1/", StringComparison.Ordinal) ? "{\"error\":{\"message\":\"invalid api key\"}}" : null,
        _ => 401);

    [Fact]
    public async Task ARefusedCheck_IsNamed_NotCalledUnreachable()
    {
        using var server = KeyRefused();
        var client = OpenAi(server);

        var ok = await client.CheckConnectionAsync(server.BaseUrl, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("401", client.ConnectionRefusal);
        var lost = Strings.MsgConnectionLost(server.BaseUrl, "OpenAI-compatible", client.ConnectionRefusal);
        Assert.Contains("401", lost);
        Assert.NotEqual(Strings.MsgConnectionGuardFailed(server.BaseUrl, "OpenAI-compatible"), lost);
    }

    [Fact]
    public async Task RefusedChecks_LeaveTheChatItsOwnAnswer()
    {
        using var server = KeyRefused();
        var client = OpenAi(server);

        for (var i = 0; i < 6; i++) await client.CheckConnectionAsync(server.BaseUrl, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            "m", [new ChatMessageDto("user", "hi")], EmptyToolRegistry.Instance, null, CancellationToken.None));

        Assert.Contains("invalid api key", ex.Message);                    // the server's own words, not the breaker's
        Assert.DoesNotContain(Strings.MsgCircuitOpen, ex.Message);
    }

    [Fact]
    public async Task Ollama_ACheckRefusedAtItsPath_IsNamedToo()
    {
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/api/tags", StringComparison.Ordinal) ? "404 page not found" : null, _ => 404);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        Assert.False(await client.CheckConnectionAsync(server.BaseUrl, CancellationToken.None));
        Assert.Contains("404", client.ConnectionRefusal);
    }

    [Fact]
    public async Task AServerThatAnswersAgain_ClearsTheRefusal()
    {
        // Reference arm: the refusal is the LAST check's — a healthy check forgets it, and the plain "cannot reach"
        // message stays the one for a server that did not answer.
        var refuse = true;
        using var server = new LoopbackHttpServer(
            path => !path.StartsWith("/v1/models", StringComparison.Ordinal) ? null
                  : refuse ? "{\"error\":\"invalid api key\"}" : "{\"data\":[{\"id\":\"m\"}]}",
            _ => refuse ? 401 : 200);
        var client = OpenAi(server);

        await client.CheckConnectionAsync(server.BaseUrl, CancellationToken.None);
        Assert.NotNull(client.ConnectionRefusal);                                                    // witness
        refuse = false;
        Assert.True(await client.CheckConnectionAsync(server.BaseUrl, CancellationToken.None));

        Assert.Null(client.ConnectionRefusal);
        Assert.Equal(Strings.MsgConnectionGuardFailed("u", "b"), Strings.MsgConnectionLost("u", "b", refusal: null));
    }

    [Theory]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs")]
    [InlineData("Inferpal.Host", "HostServer.cs")]
    public void BothFrontEnds_SayARefusalAsOne(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, Path.Combine(parts)));

        Assert.Contains("ConnectionTransition.Lost", code, StringComparison.Ordinal);                   // WITNESS
        Assert.Contains("Strings.MsgConnectionLost(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Strings.MsgConnectionGuardFailed(", code, StringComparison.Ordinal);
    }
}
