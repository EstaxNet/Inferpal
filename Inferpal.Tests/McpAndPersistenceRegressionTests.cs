using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Mcp;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What MCP servers, rules, the token store and the config receive from outside — a third-party
/// server, a hand-written file, the other editor — and must not misread.
/// </summary>
public class McpAndPersistenceRegressionTests
{
    // ── MCP over HTTP ──────────────────────────────────────────────────────────

    private sealed class Stub(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            return respond(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        }
    }

    private static long IdOf(string body)    => JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64();
    private static string MethodOf(string b) => JsonDocument.Parse(b).RootElement.GetProperty("method").GetString()!;

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage SseEvents(params string[] events)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(events.Select(e => $"event: message\ndata: {e}\n\n")), Encoding.UTF8),
        };
        r.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return r;
    }

    private static Func<string, HttpResponseMessage> Server(Func<string, HttpResponseMessage> toolsCall) => body => MethodOf(body) switch
    {
        "initialize"                => Json($$"""{ "jsonrpc":"2.0", "id":{{IdOf(body)}}, "result":{ "protocolVersion":"2024-11-05" } }"""),
        "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
        "tools/call"                => toolsCall(body),
        _                           => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private static McpHttpClient HttpClient(Func<string, HttpResponseMessage> toolsCall) =>
        new(new McpServerConfig("remote", null, [], new Dictionary<string, string>(), Url: "https://mcp.example.com/mcp"),
            new Stub(Server(toolsCall)));

    private static JsonElement NoArgs() => JsonDocument.Parse("{}").RootElement;

    /// <summary>
    /// A server that sends the id back as a STRING (<c>"id":"3"</c>): <c>TryGetInt64</c> throws on a
    /// non-numeric element, and the fallback written for that case was never reached — the call failed.
    /// </summary>
    [Fact]
    public async Task Http_AResponseWhoseIdIsAString_IsStillMatched()
    {
        await using var client = HttpClient(b =>
            Json($$"""{ "jsonrpc":"2.0", "id":"{{IdOf(b)}}", "result":{ "content":[{ "type":"text", "text":"hello" }] } }"""));
        Assert.True(await client.StartAsync(CancellationToken.None));

        var text = await client.CallToolAsync("t", NoArgs(), CancellationToken.None);

        Assert.Contains("hello", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A message carrying both <c>method</c> AND <c>id</c> is a REQUEST from the server (ping), not
    /// the response: it resolved the pending call with an empty result.
    /// </summary>
    [Fact]
    public async Task Http_AServerRequestCarryingTheSameId_IsNotTakenForTheResponse()
    {
        await using var client = HttpClient(b => SseEvents(
            $$"""{ "jsonrpc":"2.0", "id":{{IdOf(b)}}, "method":"ping" }""",
            $$"""{ "jsonrpc":"2.0", "id":{{IdOf(b)}}, "result":{ "content":[{ "type":"text", "text":"real answer" }] } }"""));
        Assert.True(await client.StartAsync(CancellationToken.None));

        var text = await client.CallToolAsync("t", NoArgs(), CancellationToken.None);

        Assert.Contains("real answer", text, StringComparison.Ordinal);
    }

    // ── Rules front matter ─────────────────────────────────────────────────────

    /// <summary>
    /// An empty front matter (<c>---</c> then <c>---</c>) gave <c>Substring(4, -1)</c>: the exception
    /// made EVERY rule disappear from the system prompt, and broke /rules, /checks and templates.
    /// </summary>
    [Fact]
    public void RulesFrontMatter_Empty_IsReadAsNoKeys_NotAnException()
    {
        var (frontMatter, body) = Inferpal.Services.Governance.RulesService.ParseFrontMatter("---\n---\nAlways write tests.");

        Assert.Empty(frontMatter);
        Assert.Equal("Always write tests.", body.Trim());
    }

    // ── Slash command arguments ────────────────────────────────────────────────

    private static object? ArgOf(SlashAction action, string name)
    {
        var tool = Assert.IsType<SlashToolAction>(action);
        return tool.Args.GetType().GetProperty(name)?.GetValue(tool.Args);
    }

    /// <summary><c>/run</c> glued the command back together after splitting it on spaces: two spaces
    /// became one, and the command executed was no longer the one typed.</summary>
    [Fact]
    public void Run_KeepsTheCommandExactlyAsTyped()
    {
        Assert.Equal("echo a  b", ArgOf(SlashCommandRouter.Route("/run echo a  b", []), "command"));
    }

    /// <summary><c>/build C:\My Project\App.sln</c> kept only <c>C:\My</c>.</summary>
    [Fact]
    public void Build_APathWithSpaces_IsKeptWhole()
    {
        Assert.Equal(@"C:\My Project\App.sln", ArgOf(SlashCommandRouter.Route(@"/build C:\My Project\App.sln", []), "path"));
    }

    // ── /check review parsing ──────────────────────────────────────────────────

    /// <summary>
    /// A line number beyond an integer (<c>bundle.min.js:1693526400123</c>) made <c>int.Parse</c>
    /// throw outside any <c>try</c>: /check failed and the review already generated was lost.
    /// </summary>
    [Fact]
    public void CheckReview_ALineNumberBeyondAnInteger_DoesNotFailTheWholeReview()
    {
        var review = Inferpal.Services.Governance.CheckReviewParser.Parse(
            "- [warning] src/bundle.min.js:1693526400123 — minified line\n- [nit] src/Alpha.cs:11 — naming",
            Inferpal.Services.Governance.DiffAnchors.Parse(""));

        Assert.Contains(review.Findings, f => f.File == "src/Alpha.cs");
    }

    // ── OAuth token store shared by two editors ────────────────────────────────

    private static McpOAuthState State(string token) => new() { AccessToken = token };

    /// <summary>
    /// Visual Studio and the VS Code host share the token file. Each kept its own cached copy and
    /// rewrote it whole: the token the other had just saved was erased.
    /// </summary>
    [Fact]
    public void TokenStore_TwoEditorsSavingDifferentServers_DoNotEraseEachOther()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcp-oauth-shared-{Guid.NewGuid():N}.dat");
        try
        {
            McpTokenStore Store() => new(path, protect: b => b, unprotect: b => b);
            var vs     = Store();
            var vscode = Store();

            vs.Save("x", State("from-vs"));
            Assert.NotNull(vscode.Get("x"));          // VS Code has read the file now
            vs.Save("y", State("from-vs-again"));
            vscode.Save("z", State("from-vscode"));   // written from a copy that never saw y

            var fresh = Store();
            Assert.NotNull(fresh.Get("y"));
            Assert.NotNull(fresh.Get("z"));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── MCP tool names ─────────────────────────────────────────────────────────

    // ── OAuth token response ───────────────────────────────────────────────────

    private sealed class NoBrowser : IAuthCodeReceiver
    {
        public string RedirectUri => "http://127.0.0.1:1/callback";
        public Task<(string Code, string State)> GetAuthorizationCodeAsync(string authorizationUrl, CancellationToken ct) =>
            throw new InvalidOperationException("no browser in a test");
    }

    private sealed class TokenEndpoint(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Json(json));
    }

    /// <summary>
    /// <c>"expires_in": "3600"</c> — a string, a shape some authorization servers send:
    /// <c>TryGetInt32</c> throws on a non-numeric element, and refreshing the token failed.
    /// </summary>
    [Fact]
    public async Task OAuth_AnExpiresInSentAsAString_IsRead()
    {
        var flow  = new McpOAuthFlow(new NoBrowser(), new TokenEndpoint("""{ "access_token":"AT2", "expires_in":"3600" }"""));
        var state = new McpOAuthState { ClientId = "c", RefreshToken = "rt", TokenEndpoint = "https://auth.example.com/token" };

        var refreshed = await flow.RefreshAsync(state, CancellationToken.None);

        Assert.NotNull(refreshed);
        Assert.NotNull(refreshed!.ExpiresAtUtc);
    }

    private sealed class FakeClient(string name) : IMcpClient
    {
        public string ServerName => name;
        public string? LastError { get; set; }
        public bool NeedsAuthorization { get; set; }
        public event Action? ToolsChanged;
        public event Action? Closed;
        public Task<bool> StartAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<McpToolInfo>?>([new McpToolInfo("t", "desc", JsonDocument.Parse("{}").RootElement.Clone())]);
        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct) => Task.FromResult(name);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Touch() { ToolsChanged?.Invoke(); Closed?.Invoke(); }
    }

    private sealed class Approve : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
            => Task.FromResult(true);
    }

    /// <summary>
    /// <c>my-server</c> and <c>my.server</c> both normalise to <c>mcp__my_server__t</c>: the model
    /// received two definitions with the same name, and every call went to the first server.
    /// </summary>
    [Fact]
    public async Task TwoServersWhoseNamesNormaliseAlike_KeepDistinctToolNames()
    {
        var config = new InferpalConfig
        {
            McpServersJson = """{ "my-server": { "command": "x" }, "my.server": { "command": "x" } }""",
        };
        var svc = new McpToolService(config, new Approve(), c => new FakeClient(c.Name), [TimeSpan.FromMilliseconds(5)]);
        config.McpEnabled = true;
        await svc.RefreshAsync();

        var names = svc.Tools.Select(t => t.Name).ToList();

        Assert.Equal(2, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    // ── MCP reconnection ───────────────────────────────────────────────────────

    private sealed class ScriptedClient(string name, bool dieWhileListing) : IMcpClient
    {
        public string ServerName => name;
        public string? LastError { get; set; }
        public bool NeedsAuthorization { get; set; }
        public event Action? ToolsChanged;
        public event Action? Closed;
        public Task<bool> StartAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct)
        {
            if (dieWhileListing)
            {
                // The process exits while its tools are being listed: Closed fires, the listing is empty.
                Closed?.Invoke();
                return Task.FromResult<IReadOnlyList<McpToolInfo>?>([]);
            }
            return Task.FromResult<IReadOnlyList<McpToolInfo>?>(
                [new McpToolInfo("t", "desc", JsonDocument.Parse("{}").RootElement.Clone())]);
        }

        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct) => Task.FromResult("ok");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Die() => Closed?.Invoke();
        public void Touch() => ToolsChanged?.Invoke();
    }

    /// <summary>
    /// A server that died again while its reconnect was listing tools: that <c>Closed</c> arrived while
    /// the reconnect still held its slot and was ignored, so the server was published "connected" with
    /// zero tools, and nothing ever reconnected it.
    /// </summary>
    [Fact]
    public async Task AServerThatDiesDuringItsReconnect_IsReconnectedAgain_NotLeftConnectedWithNoTools()
    {
        var clients = new List<ScriptedClient>();
        var config  = new InferpalConfig { McpServersJson = """{ "flaky": { "command": "x" } }""" };
        var svc = new McpToolService(config, new Approve(), c =>
        {
            lock (clients)
            {
                // #1 healthy, #2 dies while its tools are listed, #3 onwards healthy.
                var client = new ScriptedClient(c.Name, dieWhileListing: clients.Count == 1);
                clients.Add(client);
                return client;
            }
        }, [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5)]);
        config.McpEnabled = true;
        await svc.RefreshAsync();
        Assert.Single(svc.Tools);

        clients[0].Die();

        int Created() { lock (clients) return clients.Count; }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !(Created() >= 3 && svc.Tools.Count == 1 && svc.Status.Single().Connected))
            await Task.Delay(20);

        Assert.True(Created() >= 3, $"only {Created()} client(s) created: the death during the reconnect was ignored");
        Assert.Single(svc.Tools);
        Assert.True(svc.Status.Single().Connected);
    }
}

/// <summary>The config written by a newer version, read back and saved by this one.</summary>
[Collection(GlobalConfigPathCollection.Name)]
public class ConfigForwardCompatibilityTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _path =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"forward-{Guid.NewGuid():N}.json");

    public ConfigForwardCompatibilityTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { File.Delete(_path); } catch { }
    }

    /// <summary>
    /// A recent VS Code extension writes a new setting; an older Visual Studio saves — and the file was
    /// read back through the typed class: the setting it does not know disappeared.
    /// </summary>
    [Fact]
    public void ASave_KeepsASettingThisVersionDoesNotKnow()
    {
        new InferpalConfig { DefaultModel = "a" }.Save();
        var json = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        json["settingFromANewerVersion"] = 42;
        File.WriteAllText(_path, json.ToJsonString());

        var mine = InferpalConfig.Load();
        mine.DefaultModel = "b";
        mine.Save();

        var onDisk = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        Assert.Equal("b", onDisk["DefaultModel"]?.GetValue<string>() ?? onDisk["defaultModel"]?.GetValue<string>());
        Assert.Equal(42, onDisk["settingFromANewerVersion"]?.GetValue<int>());
    }
}
