using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// One line of an inline completion's stream that cannot be read is skipped, as the chat's stream and the OpenAI-compatible
/// completion skip it: the suggestion goes on with the lines after it, never ends as cut.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class FimBrokenLineTests : IDisposable
{
    private readonly SignalScratchDir _signals = new();

    public void Dispose() => _signals.Dispose();

    [Fact]
    public async Task ABrokenLine_IsSkipped_AndTheLinesAfterItStillCome()
    {
        using var server = new LoopbackHttpServer(p => p.StartsWith("/api/generate", StringComparison.Ordinal)
            ? "{\"response\":\"a + \",\"done\":false}\n{\"respo\n{\"response\":\"b\",\"done\":true}\n"
            : null);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });
        var tokens = new List<string>();

        await client.StreamFimAsync("def add(a, b):\n    return ", "", 8, 0.2, t => tokens.Add(t), CancellationToken.None);

        Assert.Equal(["a + ", "b"], tokens);
    }
}
