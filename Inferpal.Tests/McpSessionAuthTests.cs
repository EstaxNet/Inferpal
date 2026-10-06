using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Execution;
using Inferpal.Services.Mcp;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An MCP server's sign-in, from the moment it stops being accepted to the moment its server is gone.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A server that refused the token in the MIDDLE of a session (expired, revoked) stayed "Connected": the client
/// noted that a sign-in was needed and nobody read it, so the card offered no "Sign in" and every call of its tools
/// failed with a 401 the model could do nothing about.
/// </para>
/// <para>
/// ⚠ And a server taken out of the configuration left its sign-in on disk for good — access and refresh tokens of a
/// service the user no longer uses, kept by a store that nothing ever pruned.
/// </para>
/// </remarks>
public sealed class McpSessionAuthTests : IDisposable
{
    private readonly string _storePath = Path.Combine(Path.GetTempPath(), $"inferpal-mcp-oauth-{Guid.NewGuid():N}.dat");

    public void Dispose()
    {
        try { File.Delete(_storePath); } catch { }
    }

    private McpTokenStore Store() => new(_storePath, protect: b => b, unprotect: b => b);

    private sealed class Approves : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false) =>
            Task.FromResult(true);
    }

    private sealed class Token : IMcpTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult<string?>("access-token");
    }

    /// <summary>A Streamable HTTP server: the handshake and the listing answer, a tool call is refused with 401.</summary>
    private sealed class RefusesTheCall(HttpStatusCode callStatus) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var method = doc.RootElement.GetProperty("method").GetString();
            var id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetInt64() : 0;
            return method switch
            {
                "initialize" => Json($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"protocolVersion":"2024-11-05"}}"""),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                "tools/list" => Json($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"tools":[{"name":"search","inputSchema":{"type":"object"}}]}}"""),
                "tools/call" => new HttpResponseMessage(callStatus)
                {
                    Content = new StringContent("""{"error":"invalid_token"}""", Encoding.UTF8, "application/json"),
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static InferpalConfig OneRemoteServer(string name = "remote") => new()
    {
        McpEnabled     = true,
        McpServersJson = $$"""{ "{{name}}": { "url": "https://mcp.example.com/mcp" } }""",
    };

    private static async Task<McpServerStatus> WaitForAsync(McpToolService service, Func<McpServerStatus, bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (service.Status.FirstOrDefault() is { } s && done(s)) return s;
            await Task.Delay(20);
        }
        return service.Status.Single();
    }

    // ── A token refused mid-session ──────────────────────────────────────────

    [Fact]
    public async Task ATokenRefusedMidSession_AsksForSignInAgain_InsteadOfStayingConnected()
    {
        var service = new McpToolService(OneRemoteServer(), new Approves(),
            cfg => new McpHttpClient(cfg, new RefusesTheCall(HttpStatusCode.Unauthorized), new Token()),
            tokenStore: Store());
        await service.RefreshAsync();
        Assert.True(service.Status.Single().Connected);   // WITNESS: the server started and listed its tool
        var tool = Assert.Single(service.Tools);

        try { await tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None); }
        catch (Exception) { /* the call itself fails: what matters is what the service says afterwards */ }

        var status = await WaitForAsync(service, s => s.AuthRequired);
        Assert.False(status.Connected);
        Assert.True(status.AuthRequired);
        Assert.Equal("authorization required", McpToolService.NotConnectedReason(status));
        Assert.Empty(service.Tools);   // its tools stop being offered: every call of them would be refused
    }

    [Fact]
    public async Task AToolCallThatFailsOtherwise_LeavesTheServerConnected()
    {
        // Reference arm: a server error is not a refused sign-in.
        var service = new McpToolService(OneRemoteServer(), new Approves(),
            cfg => new McpHttpClient(cfg, new RefusesTheCall(HttpStatusCode.InternalServerError), new Token()),
            tokenStore: Store());
        await service.RefreshAsync();
        var tool = Assert.Single(service.Tools);

        try { await tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None); }
        catch (Exception) { }
        await Task.Delay(200);

        var status = service.Status.Single();
        Assert.True(status.Connected);
        Assert.False(status.AuthRequired);
    }

    // ── A server list that is not an object ─────────────────────────────────

    /// <summary>
    /// ⚠ A list that is not an object (an array, a string) was returned empty in silence: every server vanished and the
    /// list read as one with no server at all — no rejection on the cards, nothing in /diagnostics.
    /// </summary>
    [Fact]
    public void AServerListThatIsNotAnObject_IsRejectedByName()
    {
        var servers = McpServerConfig.Parse("""[ { "kept": { "url": "https://mcp.example.com/mcp" } } ]""", out var rejected);

        Assert.Empty(servers);
        var why = Assert.Single(rejected);
        Assert.Equal(McpServerConfig.WholeList, why.Name);
        Assert.Contains("not an object", why.Reason, StringComparison.Ordinal);
    }

    // ── A removed server's sign-in ───────────────────────────────────────────

    /// <summary>A client that never starts: these tests are about the store, not the network.</summary>
    private sealed class Offline(McpServerConfig cfg) : IMcpClient
    {
        public string ServerName => cfg.Name;
        public string? LastError => "offline";
        public bool NeedsAuthorization => false;
        public string? ResourceMetadataUrl => null;
        public event Action? ToolsChanged { add { } remove { } }
        public event Action? Closed { add { } remove { } }
        public Task<bool> StartAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>?>([]);
        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct) => Task.FromResult("");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static McpOAuthState SignedIn() => new()
    {
        AccessToken = "a", RefreshToken = "r", Resource = "https://mcp.example.com/mcp",
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
    };

    private async Task<McpTokenStore> AfterARefresh(InferpalConfig config)
    {
        var store = Store();
        store.Save("removed", SignedIn());
        store.Save("kept", SignedIn());
        var service = new McpToolService(config, new Approves(), cfg => new Offline(cfg), tokenStore: store);
        await service.RefreshAsync();
        return Store();   // read back from the file, as the next session would
    }

    [Fact]
    public async Task AServerTakenOutOfTheConfiguration_TakesItsSignInWithIt()
    {
        var store = await AfterARefresh(OneRemoteServer("kept"));

        Assert.NotNull(store.Get("kept"));   // WITNESS: the store was read and kept what is configured
        Assert.Null(store.Get("removed"));
    }

    [Theory]
    [InlineData("""{ "kept": { "url": "https://mcp.example.com/mcp" }, """, true)]        // unreadable list
    [InlineData("""{ "kept": { "url": "https://mcp.example.com/mcp" } }""", false)]      // MCP turned off
    [InlineData("""{ "kept": { "url": "https://mcp.example.com/mcp" }, "removed": { "cmd": "x" } }""", true)]   // malformed entry
    [InlineData("""[ { "kept": { "url": "https://mcp.example.com/mcp" } } ]""", true)]                       // not an object
    public async Task ASignInIsKept_WhenItsServerIsNotKnownToBeGone(string json, bool enabled)
    {
        // Reference arms: an unreadable list, MCP turned off and an entry still being written are not removals.
        var store = await AfterARefresh(new InferpalConfig { McpEnabled = enabled, McpServersJson = json });

        Assert.NotNull(store.Get("kept"));
        Assert.NotNull(store.Get("removed"));
    }
}
