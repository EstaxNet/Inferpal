using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The first run's search for a model server (<see cref="ServerScan"/>) and its proposal for the one that answers
/// (<see cref="FirstRunPlanner"/>): each place says what it answered, and the proposal is the settings' own.
/// </summary>
public sealed class FirstRunScanTests
{
    private const string OllamaTags =
        "{\"models\":[{\"name\":\"qwen3.8:27b\",\"size\":17700000000},{\"name\":\"nomic-embed-text:latest\",\"size\":274000000}]}";

    private static IInferenceProvider Create(InferpalConfig c) => InferenceProviderFactory.Create(c);

    [Fact]
    public void ThePlaces_AreTheUsualPorts_WithAConfiguredAddressFirst()
    {
        var usual = ServerScan.Places("http://localhost:11434");
        Assert.Equal(["http://localhost:11434", "http://localhost:1234", "http://localhost:9931",
                      "http://localhost:8080", "http://localhost:8000"], usual.Select(p => p.Url));

        var mine = ServerScan.Places("http://192.168.1.2:11434/v1");
        Assert.Equal(("http://192.168.1.2:11434", (string?)null), mine[0]);
        Assert.Equal(6, mine.Count);
    }

    [Fact]
    public async Task AModelServer_Answers_WithItsTypeAndItsModels()
    {
        using var server = new LoopbackHttpServer(p => p.StartsWith("/api/tags", StringComparison.Ordinal) ? OllamaTags : null);

        var place = await ServerScan.ExamineAsync(server.BaseUrl, "ollama", null, Create, CancellationToken.None);

        Assert.Equal(ServerPlaceOutcome.Answers, place.Outcome);
        Assert.Equal("ollama", place.Provider);
        Assert.Equal(1, place.ChatModels);
        Assert.Equal(1, place.SearchModels);
    }

    [Fact]
    public async Task SomethingThatIsNotAModelServer_IsToldApart_FromNothingListening()
    {
        using var page = new LoopbackHttpServer(_ => null);    // answers 404 everywhere: a web server, not ours
        var notOurs = await ServerScan.ExamineAsync(page.BaseUrl, null, null, Create, CancellationToken.None);

        var silent = await ServerScan.ExamineAsync($"http://127.0.0.1:{FreePort()}", null, null, Create, CancellationToken.None);

        Assert.Equal(ServerPlaceOutcome.NotAModelServer, notOurs.Outcome);
        Assert.Equal(ServerPlaceOutcome.NothingListening, silent.Outcome);
    }

    /// <summary>A usual port that gives no HTTP answer (a firewall, WSL's mirrored network) is asked once: the other paths
    /// go to the same socket, and four timeouts made the first run's search wait four times as long. An address the person
    /// gave is asked on every path: over a slow link the first request can fail where the next ones answer.</summary>
    [Fact]
    public async Task AUsualPortWithoutAnAnswer_IsAskedOnce_AnAddressGiven_OnEveryPath()
    {
        Assert.Equal(["/api/tags"], await PathsAskedOfASilentPort(expected: "ollama"));
        Assert.Equal(["/api/tags", "/api/v0/models", "/api/v1/models", "/v1/models"], await PathsAskedOfASilentPort(expected: null));
    }

    /// <summary>The paths the examination of a port asks for, when that port closes each connection without answering.
    /// Paths, not connections: the HTTP client may ask once more on a new connection, never for another path.</summary>
    private static async Task<string[]> PathsAskedOfASilentPort(string? expected)
    {
        const string Witness = "WITNESS";
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        var paths = new List<string>();
        var accepting = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var c = await silent.AcceptTcpClientAsync();
                    using var reader = new StreamReader(c.GetStream());
                    if (await reader.ReadLineAsync() is { } line) paths.Add(line.Split(' ') is [_, var path, ..] ? path : line);
                }
                catch (IOException) { }        // a connection its client already gave up on
                catch (SocketException) { }
                catch (Exception) { return; }  // the listener stopped
            }
        });
        try
        {
            var place = await ServerScan.ExamineAsync($"http://127.0.0.1:{port}", expected, null, Create, CancellationToken.None);
            Assert.Equal(ServerPlaceOutcome.NothingListening, place.Outcome);

            // ⚠ Under load the examination can return before the listener read its requests: one more connection, ours,
            // is taken after all of them (the backlog is first in, first out), and its close says the list is complete.
            using var witness = new TcpClient();
            await witness.ConnectAsync(IPAddress.Loopback, port);
            var stream = witness.GetStream();
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(Witness + "\r\n"));
            var closed = stream.ReadAsync(new byte[1]).AsTask();
            Assert.True(await Task.WhenAny(closed, Task.Delay(TimeSpan.FromSeconds(30))) == closed, "the listener never took the witness");
            return [.. paths.Where(p => p != Witness).Distinct().Order(StringComparer.Ordinal)];
        }
        finally
        {
            silent.Stop();
            await accepting;
        }
    }

    /// <summary>A server whose models cannot be listed answers with its models unknown, never "0 chat models": that would
    /// send the person to download a model it may have.</summary>
    [Fact]
    public async Task AServerWhoseModelsCannotBeListed_SaysSo_InsteadOfZero()
    {
        using var server = new LoopbackHttpServer(p => p.StartsWith("/api/tags", StringComparison.Ordinal) ? OllamaTags : null);
        var failing = new FakeInferenceProvider { BeforeListModels = () => throw new HttpRequestException("timed out") };

        var place = await ServerScan.ExamineAsync(server.BaseUrl, "ollama", null, _ => failing, CancellationToken.None);

        Assert.Equal(ServerPlaceOutcome.Answers, place.Outcome);
        Assert.Equal(Inferpal.Localization.Strings.SetupPlaceAnswersUnlisted("timed out"), place.Status);
        Assert.Null(SetupPlaceDto.Of(place).AlsoAnswering);
    }

    /// <summary>The server offered is the configured one when it answers — the person's choice — else the first with a
    /// chat model: one choice for VS Code's card, the ACP setup and Visual Studio.</summary>
    [Fact]
    public void TheServerOffered_IsTheConfiguredOne_WhenItAnswers()
    {
        ServerPlace[] places =
        [
            new("http://localhost:11434", "ollama", ServerPlaceOutcome.Answers, "ollama", null, ChatModels: 3),
            new("http://localhost:1234", "lmstudio", ServerPlaceOutcome.Answers, "lmstudio", null, ChatModels: 2),
        ];

        Assert.Equal("http://localhost:1234", ServerScan.Offered(places, "http://localhost:1234/v1")?.Url);
        Assert.Equal("http://localhost:11434", ServerScan.Offered(places, "http://192.168.1.2:11434")?.Url);
    }

    [Fact]
    public async Task AServerThatWantsAKey_SaysSo()
    {
        using var server = new LoopbackHttpServer(_ => "{\"error\":\"unauthorized\"}", _ => 401);

        var place = await ServerScan.ExamineAsync(server.BaseUrl, null, null, Create, CancellationToken.None);

        Assert.Equal(ServerPlaceOutcome.NeedsKey, place.Outcome);
        Assert.Contains("401", place.Detail);
    }

    /// <summary>⚠ A key belongs to its server: the configured key goes to the configured address, never to the others.</summary>
    [Fact]
    public async Task TheConfiguredKey_GoesToTheConfiguredAddressOnly()
    {
        using var mine  = new LoopbackHttpServer(p => p.StartsWith("/api/tags", StringComparison.Ordinal) ? OllamaTags : null);
        using var other = new LoopbackHttpServer(p => p.StartsWith("/api/tags", StringComparison.Ordinal) ? OllamaTags : null);
        var keys = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        await ServerScan.ScanAsync([(mine.BaseUrl, "ollama"), (other.BaseUrl, "ollama")], mine.BaseUrl, "k-1",
            c => { keys[c.BaseUrl] = c.ApiKey ?? ""; return Create(c); }, CancellationToken.None);

        Assert.Equal("k-1", keys[mine.BaseUrl]);
        Assert.Equal("", keys[other.BaseUrl]);
    }

    [Fact]
    public void AServerWithAChatModel_GetsTheSettingsProposal_AndWhetherItFits()
    {
        InstalledModelInfo[] installed =
        [
            new("qwen3.8:27b", 17_700_000_000), new("llama3.1:8b", 4_900_000_000), new("nomic-embed-text:latest", 274_000_000),
        ];

        var plan = FirstRunPlanner.Plan(installed, "ollama", "http://localhost:11434", budgetGb: 24, canDownload: true);

        Assert.Equal(ModelCatalog.PickBestChatModel(["qwen3.8:27b", "llama3.1:8b"]), plan.ChatModel);
        Assert.Equal("nomic-embed-text:latest", plan.SearchModel);
        Assert.Equal(2, plan.ChatCount);
        Assert.True(plan.NeededGb > 0);
        Assert.Empty(plan.Downloads);
    }

    [Theory]
    [InlineData(24, new[] { "qwen3.8:27b", "gemma4:12b" })]
    [InlineData(12, new[] { "gemma4:12b" })]
    [InlineData(0,  new[] { "qwen3.8:27b", "gemma4:12b" })]   // unknown card: every measured download, with its card
    [InlineData(8,  new string[0])]                            // smaller than every measured setup: nothing to offer
    public void OllamaWithoutAChatModel_OffersTheDownloadsThatFitTheCard(double budgetGb, string[] tags)
    {
        InstalledModelInfo[] installed = [new("nomic-embed-text:latest", 274_000_000)];

        var plan = FirstRunPlanner.Plan(installed, "ollama", "http://localhost:11434", budgetGb, canDownload: true);

        Assert.Null(plan.ChatModel);
        Assert.Equal(tags, plan.Downloads.Select(d => d.Tag));
        Assert.Equal(["nomic-embed-text:latest"], plan.Listed);
        // A card too small for every measured download is said as that, never as a server that cannot download.
        Assert.Equal(tags.Length == 0, plan.NoneFits);
        Assert.Equal(tags.Length == 0 ? Inferpal.Localization.Strings.SetupNoneFits("8", "Ollama") : null,
                     SetupPlanDto.Of(plan).NoDownload);
    }

    [Fact]
    public void AnotherServerWithoutAChatModel_OffersNoDownload()
    {
        var plan = FirstRunPlanner.Plan([], "lmstudio", "http://localhost:1234", 24, canDownload: true);

        Assert.Null(plan.ChatModel);
        Assert.Empty(plan.Downloads);
    }

    /// <summary>One list of what is recommended: a download is always one of the recommended agents.</summary>
    [Fact]
    public void EveryDownload_IsARecommendedAgent()
    {
        Assert.NotEmpty(ModelAdvisor.OllamaDownloads);
        foreach (var d in ModelAdvisor.OllamaDownloads)
            Assert.Contains(ModelAdvisor.RecommendedAgents, a => a.Name == d.Name);
    }

    /// <summary>A first run that chose nothing takes the server found — the one with a chat model first; a server the
    /// person typed, or a key, is theirs and is never replaced.</summary>
    [Fact]
    public void AFirstRunThatChoseNothing_TakesTheServerFound_AndNothingElseDoes()
    {
        ServerPlace[] places =
        [
            new("http://localhost:11434", "ollama", ServerPlaceOutcome.NothingListening, null, null),
            new("http://localhost:1234", "lmstudio", ServerPlaceOutcome.Answers, "lmstudio", null, SearchModels: 1),
            new("http://localhost:8000", "openai-compatible", ServerPlaceOutcome.Answers, "openai-compatible", null, ChatModels: 2),
        ];

        var fresh = new InferpalConfig { IsFirstRun = true };
        Assert.Equal("http://localhost:8000", ServerScan.ToAdopt(fresh, places)?.Url);
        Assert.Equal("http://localhost:1234", ServerScan.ToAdopt(fresh, places[..2])?.Url);
        Assert.Null(ServerScan.ToAdopt(new InferpalConfig { IsFirstRun = true, BaseUrl = "http://192.168.1.2:11434" }, places));
        Assert.Null(ServerScan.ToAdopt(new InferpalConfig { IsFirstRun = true, ApiKey = "k-1" }, places));
        Assert.Null(ServerScan.ToAdopt(new InferpalConfig { IsFirstRun = false }, places));
        // The first-run flag stays set for anyone set up outside Visual Studio's first run: a chosen model is a setup.
        Assert.Null(ServerScan.ToAdopt(new InferpalConfig { IsFirstRun = true, DefaultModel = "devstral" }, places));
    }

    [Theory]
    [InlineData("pulling 2b5d1e (37%)", 37)]
    [InlineData("verifying sha256 digest", null)]
    [InlineData("pulling manifest (100%) ", 100)]
    public void APullStatus_GivesItsPercentage(string status, int? percent) =>
        Assert.Equal(percent, HostServer.PercentOf(status));

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
