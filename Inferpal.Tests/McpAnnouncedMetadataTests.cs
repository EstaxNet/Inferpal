using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Execution;
using Inferpal.Services.Mcp;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ An MCP server that refuses with 401 may say WHERE its authorization metadata lives — the <c>resource_metadata</c>
/// of its <c>WWW-Authenticate</c> header (RFC 9728), which the MCP authorization spec has the client use. The parser
/// and the sign-in flow both supported it, and nothing carried the address from one to the other: the sign-in only
/// ever tried the default well-known path, so a server publishing its metadata elsewhere could not be signed in to.
/// </summary>
public class McpAnnouncedMetadataTests
{
    private const string Announced = "https://auth.example.com/.well-known/oauth-protected-resource/mcp";

    private sealed class Refusing(string challenge) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (challenge.Length > 0) response.Headers.TryAddWithoutValidation("WWW-Authenticate", challenge);
            return Task.FromResult(response);
        }
    }

    private sealed class NoToken : IMcpTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private static McpServerConfig Remote() =>
        new("remote", null, [], new Dictionary<string, string>(), Url: "https://mcp.example.com/mcp");

    [Fact]
    public async Task ARefusal_KeepsTheMetadataAddressTheServerAnnounced()
    {
        await using var client = new McpHttpClient(
            Remote(), new Refusing($"Bearer realm=\"mcp\", resource_metadata=\"{Announced}\""), new NoToken());

        Assert.False(await client.StartAsync(CancellationToken.None));
        Assert.True(client.NeedsAuthorization);   // WITNESS: the refusal went down the 401 path
        Assert.Equal(Announced, client.ResourceMetadataUrl);
    }

    [Fact]
    public async Task ARefusalThatAnnouncesNothing_LeavesTheDefaultToTheSignIn()   // reference arm
    {
        await using var client = new McpHttpClient(Remote(), new Refusing("Bearer realm=\"mcp\""), new NoToken());

        Assert.False(await client.StartAsync(CancellationToken.None));
        Assert.Null(client.ResourceMetadataUrl);
    }

    private sealed class Approves : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, Services.CodeActions.DiffInfo? diff = null,
                                               bool forcePrompt = false) => Task.FromResult(true);
    }

    /// <summary>A server that refused us and said where to look.</summary>
    private sealed class RefusedClient(string name, string? announced) : IMcpClient
    {
        public string ServerName => name;
        public string? LastError => "401 Unauthorized";
        public bool NeedsAuthorization => true;
        public string? ResourceMetadataUrl => announced;
        public event Action? ToolsChanged { add { } remove { } }
        public event Action? Closed { add { } remove { } }
        public Task<bool> StartAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<McpToolInfo>?>(null);
        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct) =>
            Task.FromResult(string.Empty);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(Announced)]
    [InlineData(null)]
    public async Task TheSignIn_StartsFromWhatTheServerAnnounced(string? announced)
    {
        const string json = """{ "remote": { "url": "https://mcp.example.com/mcp" } }""";
        var config = new InferpalConfig { McpServersJson = json, McpEnabled = true };
        var mcp    = new McpToolService(config, new Approves(), _ => new RefusedClient("remote", announced),
                                        [TimeSpan.FromMilliseconds(5)]);
        await mcp.RefreshAsync();

        var server = mcp.OAuthServerFor(McpServerConfig.Parse(json).Single());
        Assert.Equal(announced, server.ResourceMetadataUrl);
    }
}
