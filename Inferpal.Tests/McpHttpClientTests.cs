using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

public class McpHttpClientTests
{
    // ── Fake transport ──────────────────────────────────────────────────────

    private sealed class StubHandler : HttpMessageHandler
    {
        /// <summary>Handles POST (JSON-RPC) requests, keyed by the request body.</summary>
        public Func<string, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        /// <summary>Handles the optional GET notification stream; defaults to "not supported".</summary>
        public Func<HttpResponseMessage> RespondGet { get; set; } = () => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        // Recorded for POSTs only, so the background GET listener doesn't perturb the request sequence.
        public List<string?> SessionHeaders { get; } = [];
        public List<string?> AuthHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return RespondGet();

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            SessionHeaders.Add(request.Headers.TryGetValues("Mcp-Session-Id", out var s) ? s.FirstOrDefault() : null);
            AuthHeaders.Add(request.Headers.TryGetValues("Authorization", out var a) ? a.FirstOrDefault() : null);
            return Respond(body);
        }
    }

    private static long IdOf(string body)   => JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64();
    private static string MethodOf(string b) => JsonDocument.Parse(b).RootElement.GetProperty("method").GetString()!;

    private static HttpResponseMessage Json(string json, string? session = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (session is not null) r.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        return r;
    }

    private static HttpResponseMessage Sse(string json)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"event: message\ndata: {json}\n\n", Encoding.UTF8),
        };
        r.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return r;
    }

    private static HttpResponseMessage Result(string body, string innerJson) =>
        Json($$"""{ "jsonrpc": "2.0", "id": {{IdOf(body)}}, "result": {{innerJson}} }""");

    private static McpServerConfig HttpCfg(IReadOnlyDictionary<string, string>? headers = null) =>
        new("remote", null, [], new Dictionary<string, string>(),
            Url: "https://mcp.example.com/mcp", Headers: headers);

    private static McpHttpClient Client(StubHandler handler, IReadOnlyDictionary<string, string>? headers = null) =>
        new(HttpCfg(headers), handler);

    /// <summary>A responder that handles the handshake, with a customisable tools/list and tools/call.</summary>
    private static Func<string, HttpResponseMessage> Responder(
        Func<string, HttpResponseMessage>? toolsList = null,
        Func<string, HttpResponseMessage>? toolsCall = null,
        string? session = "sess-1")
        => body => MethodOf(body) switch
        {
            "initialize"                => Json($$"""{ "jsonrpc":"2.0", "id":{{IdOf(body)}}, "result":{ "protocolVersion":"2024-11-05" } }""", session),
            "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
            "tools/list"                => (toolsList ?? (b => Result(b, """{ "tools": [] }""")))(body),
            "tools/call"                => (toolsCall ?? (b => Result(b, """{ "content": [] }""")))(body),
            _                           => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

    private static JsonElement NoArgs() => JsonDocument.Parse("{}").RootElement;

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Initialize_CapturesSessionId_AndEchoesItPlusAuthHeader()
    {
        var handler = new StubHandler
        {
            Respond = Responder(toolsList: b => Result(b, """{ "tools": [ { "name": "do_it" } ] }""")),
        };
        await using var client = Client(handler, new Dictionary<string, string> { ["Authorization"] = "Bearer abc" });

        Assert.True(await client.StartAsync(CancellationToken.None));
        var tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.Equal("mcp__remote__do_it", McpTool.BuildName(client.ServerName, tools.Single().Name));
        // initialize carries no session yet; it's echoed on every later request.
        Assert.Equal([null, "sess-1", "sess-1"], handler.SessionHeaders);
        Assert.All(handler.AuthHeaders, h => Assert.Equal("Bearer abc", h));
    }

    [Fact]
    public async Task ListTools_ParsesSseResponse()
    {
        var handler = new StubHandler
        {
            Respond = Responder(toolsList: b => Sse($$"""{ "jsonrpc":"2.0", "id":{{IdOf(b)}}, "result":{ "tools":[ { "name":"streamed" } ] } }""")),
        };
        await using var client = Client(handler);
        await client.StartAsync(CancellationToken.None);

        var tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.Equal("streamed", tools.Single().Name);
    }

    [Fact]
    public async Task CallTool_ReturnsConcatenatedText()
    {
        var handler = new StubHandler
        {
            Respond = Responder(toolsCall: b => Result(b, """{ "content": [ { "type":"text", "text":"hello" } ] }""")),
        };
        await using var client = Client(handler);
        await client.StartAsync(CancellationToken.None);

        Assert.Equal("hello", await client.CallToolAsync("do_it", NoArgs(), CancellationToken.None));
    }

    [Fact]
    public async Task CallTool_JsonRpcError_Throws()
    {
        var handler = new StubHandler
        {
            Respond = Responder(toolsCall: b =>
                Json($$"""{ "jsonrpc":"2.0", "id":{{IdOf(b)}}, "error":{ "code":-32000, "message":"nope" } }""")),
        };
        await using var client = Client(handler);
        await client.StartAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallToolAsync("do_it", NoArgs(), CancellationToken.None));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task StartAsync_ReturnsFalse_OnHttpError()
    {
        var handler = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        await using var client = Client(handler);

        Assert.False(await client.StartAsync(CancellationToken.None));
        Assert.NotNull(client.LastError);
    }

    [Fact]
    public async Task Headers_ExpandEnvPlaceholders()
    {
        Environment.SetEnvironmentVariable("INFERPAL_TEST_TOKEN", "s3cr3t");
        try
        {
            var handler = new StubHandler { Respond = Responder() };
            await using var client = Client(handler,
                new Dictionary<string, string> { ["Authorization"] = "Bearer ${INFERPAL_TEST_TOKEN}" });

            await client.StartAsync(CancellationToken.None);

            Assert.All(handler.AuthHeaders, h => Assert.Equal("Bearer s3cr3t", h));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFERPAL_TEST_TOKEN", null);
        }
    }

    [Fact]
    public async Task ExpiredSession_404_ReinitializesAndRetries()
    {
        var inits = 0;
        var lists = 0;
        var handler = new StubHandler();
        handler.Respond = body => MethodOf(body) switch
        {
            "initialize" => Json($$"""{ "jsonrpc":"2.0", "id":{{IdOf(body)}}, "result":{} }""", session: $"s{++inits}"),
            "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
            // First tools/list with a live session 404s (server expired it); the replay succeeds.
            "tools/list" => ++lists == 1
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Result(body, """{ "tools": [ { "name": "back" } ] }"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        await using var client = Client(handler);
        await client.StartAsync(CancellationToken.None);

        var tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.Equal("back", tools.Single().Name);
        Assert.Equal(2, inits);   // initial handshake + one re-initialize after the 404
        // The retried tools/list rides the new session id.
        Assert.Equal("s2", handler.SessionHeaders.Last());
        Assert.Equal(2, inits);   // initial handshake + one re-initialize after the 404
        // The retried tools/list rides the new session id.
        Assert.Equal("s2", handler.SessionHeaders.Last());
    }

    [Fact]
    public async Task NotificationStream_RaisesToolsChanged_OnListChanged()
    {
        var handler = new StubHandler
        {
            Respond    = Responder(),
            RespondGet = () => Sse("""{ "jsonrpc": "2.0", "method": "notifications/tools/list_changed" }"""),
        };
        await using var client = Client(handler);
        var changed = new TaskCompletionSource();
        client.ToolsChanged += () => changed.TrySetResult();

        Assert.True(await client.StartAsync(CancellationToken.None));

        var done = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(changed.Task, done);
    }

    [Fact]
    public async Task NotificationStream_Unsupported_StartStillSucceeds_NoToolsChanged()
    {
        // Default RespondGet returns 405 ⇒ no notification stream.
        var handler = new StubHandler { Respond = Responder() };
        await using var client = Client(handler);
        var fired = false;
        client.ToolsChanged += () => fired = true;

        Assert.True(await client.StartAsync(CancellationToken.None));
        await Task.Delay(200);

        Assert.False(fired);
    }

    private sealed class FakeTokenProvider(string? token) : Inferpal.Services.Mcp.OAuth.IMcpTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult(token);
    }

    [Fact]
    public async Task TokenProvider_AddsBearerHeaderOnEveryRequest()
    {
        var handler = new StubHandler { Respond = Responder() };
        await using var client = new Inferpal.Services.Mcp.McpHttpClient(
            HttpCfg(), handler, new FakeTokenProvider("oauth-tok"));

        await client.StartAsync(CancellationToken.None);
        await client.ListToolsAsync(CancellationToken.None);

        Assert.All(handler.AuthHeaders, h => Assert.Equal("Bearer oauth-tok", h));
        Assert.False(client.NeedsAuthorization);
    }

    [Fact]
    public async Task TokenProvider_BearerOverridesConfiguredAuthorizationHeader()
    {
        var handler = new StubHandler { Respond = Responder() };
        await using var client = new Inferpal.Services.Mcp.McpHttpClient(
            HttpCfg(new Dictionary<string, string> { ["Authorization"] = "Bearer static" }),
            handler, new FakeTokenProvider("oauth-tok"));

        await client.StartAsync(CancellationToken.None);

        Assert.All(handler.AuthHeaders, h => Assert.Equal("Bearer oauth-tok", h));
    }

    [Fact]
    public async Task Unauthorized401_WithOAuth_SetsNeedsAuthorization()
    {
        // No token yet; server rejects with 401.
        var handler = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        await using var client = new Inferpal.Services.Mcp.McpHttpClient(
            HttpCfg(), handler, new FakeTokenProvider(null));

        var ok = await client.StartAsync(CancellationToken.None);

        Assert.False(ok);
        Assert.True(client.NeedsAuthorization);
    }

    [Fact]
    public void ExpandEnv_ReplacesKnownVars_AndBlanksUnknown()
    {
        Environment.SetEnvironmentVariable("INFERPAL_EXP_A", "bar");
        try
        {
            Assert.Equal("a-bar-b", McpHttpClient.ExpandEnv("a-${INFERPAL_EXP_A}-b"));
            Assert.Equal("x--y", McpHttpClient.ExpandEnv("x-${INFERPAL_DEFINITELY_UNSET_XYZ}-y"));
            Assert.Equal("no placeholder", McpHttpClient.ExpandEnv("no placeholder"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFERPAL_EXP_A", null);
        }
    }
}
