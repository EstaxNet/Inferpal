using System.IO;
using Inferpal.Config;
using Inferpal.Host.Acp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>Inferpal.Host --acp --setup</c>: the setup an ACP client runs in a terminal. A server is saved only once it
/// answered, and the backend saved is the one that answered.
/// </summary>
public sealed class AcpSetupTests
{
    private static Task<int> RunAsync(string typed, StringWriter output, List<InferpalConfig> saved,
                                      Func<string, (string?, string?)> probe, IReadOnlyList<string> models) =>
        AcpSetup.RunAsync(new StringReader(typed), output, CancellationToken.None,
                          probe: (url, _, _) => Task.FromResult(probe(url)),
                          listModels: (_, _) => Task.FromResult(models),
                          load: () => new InferpalConfig(),
                          save: saved.Add);

    [Fact]
    public async Task AServerThatAnswers_AndAModelPicked_AreSaved()
    {
        var saved = new List<InferpalConfig>();
        var output = new StringWriter();

        // LM Studio, its default address, the second model.
        var code = await RunAsync("2\n\n2\n", output, saved, _ => ("lmstudio", null), ["qwen-chat", "devstral", "nomic-embed-text"]);

        Assert.Equal(0, code);
        var config = Assert.Single(saved);
        Assert.Equal("lmstudio", config.Provider);
        Assert.Equal("http://localhost:1234", config.BaseUrl);
        Assert.Equal("devstral", config.DefaultModel);
        Assert.DoesNotContain("nomic-embed-text", output.ToString());   // an embedding model answers no question
    }

    [Fact]
    public async Task AnAddressNothingAnswersAt_IsAskedAgain_AndTheServerThatAnsweredIsSaved()
    {
        var saved = new List<InferpalConfig>();
        var output = new StringWriter();

        // Ollama chosen, a dead address, then one where LM Studio answers: what answered is what is saved.
        var code = await RunAsync("1\nhttp://dead:1\nhttp://live:1234\n\n", output, saved,
                                  url => url.Contains("live") ? ("lmstudio", null) : (null, null), ["devstral"]);

        Assert.Equal(0, code);
        Assert.Contains(Localization.Strings.AcpSetupNotReached("http://dead:1"), output.ToString());
        var config = Assert.Single(saved);
        Assert.Equal("lmstudio", config.Provider);
        Assert.Equal("http://live:1234", config.BaseUrl);
    }

    [Fact]
    public async Task ATerminalClosedMidway_SavesNothing_AndFails()
    {
        var saved = new List<InferpalConfig>();
        var code = await RunAsync("2\n", new StringWriter(), saved, _ => ("lmstudio", null), ["devstral"]);

        Assert.NotEqual(0, code);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task AServerWithoutAModel_SavesNothing_AndSaysSo()
    {
        var saved = new List<InferpalConfig>();
        var output = new StringWriter();
        var code = await RunAsync("1\n\n", output, saved, _ => ("ollama", null), ["nomic-embed-text"]);

        Assert.NotEqual(0, code);
        Assert.Empty(saved);
        Assert.Contains(Localization.Strings.AcpSetupNoModel("Ollama"), output.ToString());
    }
}
