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
                                      Func<string, (string?, string?)> probe, IReadOnlyList<string> models,
                                      IReadOnlyList<ServerPlace>? places = null, InferpalConfig? config = null,
                                      List<string?>? keysProbed = null) =>
        AcpSetup.RunAsync(new StringReader(typed), output, CancellationToken.None,
                          probe: (url, key, _) => { keysProbed?.Add(key); return Task.FromResult(probe(url)); },
                          listModels: (_, _) => Task.FromResult(ModelsByKind.ByName(models)),
                          load: () => config ?? new InferpalConfig(),
                          save: saved.Add,
                          // Never this machine's real ports: what answers here is the test's to say.
                          scan: (_, _) => Task.FromResult(places ?? []));

    /// <summary>The graphics memory set for one server never sizes another: a server elsewhere is set up with its card
    /// unknown, not with the old machine's figure.</summary>
    [Fact]
    public async Task TheMemorySetForOneServer_IsNotSavedWithAnother()
    {
        var saved = new List<InferpalConfig>();
        var config = new InferpalConfig { Provider = "ollama", BaseUrl = "http://192.168.1.5:11434", VramBudgetGb = 24 };

        // OpenAI-compatible, an address elsewhere, no key, the first model.
        var code = await RunAsync("3\nhttp://192.168.1.9:8000\n\n\n", new StringWriter(), saved, _ => ("openai-compatible", null),
                                  ["qwen3-coder"], config: config);

        Assert.Equal(0, code);
        Assert.Equal(("http://192.168.1.9:8000", 0.0), (saved.Single().BaseUrl, saved.Single().VramBudgetGb));
    }

    /// <summary>The key saved for one server never goes to another: a configured server down, the one the scan found on
    /// this machine is probed and saved without it. And a setup ends the first run.</summary>
    [Fact]
    public async Task TheSavedKey_StaysWithItsServer_WhenAnotherIsChosen()
    {
        var saved = new List<InferpalConfig>();
        var keys = new List<string?>();
        var config = new InferpalConfig { Provider = "openai-compatible", BaseUrl = "http://remote:8000", ApiKey = "k-1" };
        ServerPlace[] places = [new("http://localhost:1234", "lmstudio", ServerPlaceOutcome.Answers, "lmstudio", null, ChatModels: 1)];

        // Every default taken: LM Studio, found on this machine, and its first model.
        var code = await RunAsync("\n\n\n", new StringWriter(), saved, _ => ("lmstudio", null), ["devstral"], places, config, keys);

        Assert.Equal(0, code);
        Assert.Equal([null], keys);
        Assert.Equal(("http://localhost:1234", ""), (saved.Single().BaseUrl, saved.Single().ApiKey));
        Assert.False(saved.Single().IsFirstRun);
    }

    /// <summary>The setup looks first: what each place answered is said, and the server that answers is offered first,
    /// at the address it answered at.</summary>
    [Fact]
    public async Task TheServerFoundOnTheMachine_IsOfferedFirst_AtTheAddressItAnswered()
    {
        var saved = new List<InferpalConfig>();
        var output = new StringWriter();
        ServerPlace[] places =
        [
            new("http://localhost:11434", "ollama", ServerPlaceOutcome.NothingListening, null, null),
            new("http://localhost:8000", "openai-compatible", ServerPlaceOutcome.Answers, "openai-compatible", null, ChatModels: 2),
        ];

        // Every default taken: the server, its address, no key, the first model.
        var code = await RunAsync("\n\n\n\n", output, saved, _ => ("openai-compatible", null), ["qwen3-coder"], places);

        Assert.Equal(0, code);
        Assert.Contains($"Ollama · http://localhost:11434 — {Localization.Strings.SetupPlaceNothingListening}", output.ToString());
        Assert.Equal(("openai-compatible", "http://localhost:8000"), (saved.Single().Provider, saved.Single().BaseUrl));
    }

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

    /// <summary>An OpenAI-compatible server is offered llama-server's default address, as Ollama and LM Studio are theirs.</summary>
    [Fact]
    public async Task AnOpenAiCompatibleServer_IsOfferedLlamaServersDefaultAddress()
    {
        var saved = new List<InferpalConfig>();
        var probed = new List<string>();

        // OpenAI-compatible, the address offered, no key, the first model.
        var code = await RunAsync("3\n\n\n\n", new StringWriter(), saved, url => { probed.Add(url); return ("openai-compatible", null); },
                                  ["qwen3-coder"]);

        Assert.Equal(0, code);
        Assert.Equal(["http://localhost:9931"], probed);
        Assert.Equal("http://localhost:9931", Assert.Single(saved).BaseUrl);
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
