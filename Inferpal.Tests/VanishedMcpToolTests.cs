using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A tool whose MCP server went away is not a tool the model INVENTED.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Raw measurement, before the fix</b>: <c>Unknown tool: mcp__github__create_issue</c>, while
/// the very same object graph held <c>github connected=False err=the server did not start: command
/// 'x' not found</c>. The model therefore reads "you invented this name" — although it <b>read</b> it
/// in its own tool list at the start of the turn — and hunts for another name instead of saying the
/// server is down.
/// </para>
/// <para>
/// ⚠ <b>The rule existed, held by two of its THREE readers</b>: "a dead MCP server separates
/// <i>authorization is needed</i> from <i>it did not start</i>", in <c>/diagnostics</c> <b>and</b> in
/// the support bundle. The third reader — the tool call itself, the only one of the three the model
/// sees — knew nothing about it. The wording of the reason is now <b>single</b>
/// (<c>NotConnectedReason</c>), shared by the bundle and by this sentence.
/// </para>
/// <para>
/// ⚠ <b>And the name is recognised by its BUILDER</b>, never by splitting on <c>__</c>: sanitising
/// replaces every non-alphanumeric character with <c>_</c>, so a server or a tool that already holds
/// one makes any split ambiguous. <c>McpTool.BelongsTo</c> asks the prefix question with
/// <c>BuildName</c>, which cannot drift.
/// </para>
/// </remarks>
public class VanishedMcpToolTests
{
    private sealed class Approves : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, Services.CodeActions.DiffInfo? diff = null,
                                               bool forcePrompt = false) => Task.FromResult(true);
    }

    private sealed class NullEditor : Services.Editor.IEditorSurface
    {
        public bool IsAvailable => false;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<Services.Editor.ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<Services.Editor.ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<Services.Editor.EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
            Task.FromResult<Services.Editor.EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>A server that never starts — the shape of a bad command or a dead container.</summary>
    private sealed class DeadClient(string name, bool needsAuth = false) : IMcpClient
    {
        public string ServerName => name;
        public string? LastError => "the server did not start: command 'x' not found";
        public bool NeedsAuthorization => needsAuth;
        public event Action? ToolsChanged { add { } remove { } }
        public event Action? Closed { add { } remove { } }
        public Task<bool> StartAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<McpToolInfo>?>(null);
        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct) =>
            Task.FromResult(string.Empty);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A server that works, and offers exactly one tool.</summary>
    private sealed class LiveClient(string name) : IMcpClient
    {
        public string ServerName => name;
        public string? LastError => null;
        public bool NeedsAuthorization => false;
        public event Action? ToolsChanged { add { } remove { } }
        public event Action? Closed { add { } remove { } }
        public Task<bool> StartAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<McpToolInfo>?>(
                [new("list_issues", "desc", JsonDocument.Parse("{}").RootElement.Clone())]);
        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct) =>
            Task.FromResult("ok");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<(ToolRegistry Tools, McpToolService Mcp)> BuildAsync(
        string serversJson, Func<McpServerConfig, IMcpClient> factory)
    {
        var config   = new InferpalConfig { McpServersJson = serversJson, McpEnabled = true };
        var approval = new Approves();
        var mcp      = new McpToolService(config, approval, factory, [TimeSpan.FromMilliseconds(5)]);
        await mcp.RefreshAsync();

        var client = new FakeInferenceProvider();
        var editor = new NullEditor();
        var tools  = new ToolRegistry(editor, approval, config,
                                      new ProjectIndexService(client, config, new LspSemanticProvider()),
                                      client, new Services.ProjectMapService(editor), mcp,
                                      new Services.Docs.DocsIndexService(client, config));
        return (tools, mcp);
    }

    private static Task<string> Call(ToolRegistry tools, string name) =>
        tools.ExecuteAsync(name, JsonDocument.Parse("{}").RootElement, CancellationToken.None);

    private const string OneServer = """{ "github": { "command": "x" } }""";

    // ── The defect ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AToolWhoseServerIsDown_NamesTheServerAndTheReason()
    {
        var (tools, mcp) = await BuildAsync(OneServer, _ => new DeadClient("github"));
        await using var _ = mcp;

        // WITNESS: this object graph really does know the reason — without it nothing could say it.
        Assert.False(Assert.Single(mcp.Status).Connected);

        var said = await Call(tools, "mcp__github__create_issue");

        Assert.DoesNotContain("Unknown tool", said, StringComparison.Ordinal);
        Assert.Contains("github", said, StringComparison.Ordinal);
        Assert.Contains("did not start", said, StringComparison.Ordinal);
        // And the gesture: do not retry, say it. A "try again" would make the model loop.
        Assert.Contains("Do not retry", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AServerThatNeedsAuthorization_SaysThatInstead()
    {
        // Two causes, two sentences: they are not repaired in the same place.
        var (tools, mcp) = await BuildAsync(OneServer, _ => new DeadClient("github", needsAuth: true));
        await using var _ = mcp;

        var said = await Call(tools, "mcp__github__create_issue");

        Assert.Contains("authorization required", said, StringComparison.Ordinal);
    }

    // ── The reference arms ───────────────────────────────────────────────────

    [Fact]
    public async Task ANameNoServerClaims_IsStillAnUnknownTool()
    {
        // The only case that really is an invented tool. Without this arm, every unknown name could
        // become "a server went away", which is the defect rebuilt backwards.
        var (tools, mcp) = await BuildAsync(OneServer, _ => new DeadClient("github"));
        await using var _ = mcp;

        Assert.Contains("Unknown tool", await Call(tools, "search_filez"), StringComparison.Ordinal);
        Assert.Contains("Unknown tool", await Call(tools, "mcp__gitlab__create_issue"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConnectedServerThatSimplyHasNoSuchTool_SaysThat()
    {
        var (tools, mcp) = await BuildAsync(OneServer, _ => new LiveClient("github"));
        await using var _ = mcp;

        // WITNESS: the server really is up and serving its tool.
        Assert.Equal("ok", await Call(tools, "mcp__github__list_issues"));

        var said = await Call(tools, "mcp__github__create_issue");

        Assert.Contains("is not offered by", said, StringComparison.Ordinal);
        Assert.Contains("1 tool(s)", said, StringComparison.Ordinal);
        Assert.DoesNotContain("not connected", said, StringComparison.Ordinal);
    }

    // ── One wording, two readers ─────────────────────────────────────────────

    [Fact]
    public async Task TheBundleAndTheToolCall_GiveTheSameReason()
    {
        // ⚠ This is the point of the refactor: the support bundle and the sentence the model reads
        // described the same thing and could drift. The bundle had no test naming its
        // "authorization required" branch — it has one now, through this shared reader.
        var (tools, mcp) = await BuildAsync(OneServer, _ => new DeadClient("github", needsAuth: true));
        await using var _ = mcp;

        var bundle = Assert.Single(mcp.DescribeForBundle());
        var said   = await Call(tools, "mcp__github__create_issue");

        Assert.Contains("authorization required", bundle, StringComparison.Ordinal);
        Assert.Contains("authorization required", said, StringComparison.Ordinal);
    }

    // ── The name is recognised by its builder ────────────────────────────────

    [Theory]
    [InlineData("github", "mcp__github__create_issue", true)]
    [InlineData("git hub", "mcp__git_hub__create_issue", true)]   // sanitising: space → _
    [InlineData("github", "mcp__github2__create_issue", false)]
    [InlineData("github", "search_in_files", false)]
    public void ANameBelongsToItsServerByTheSameBuilder(string server, string toolName, bool expected)
    {
        Assert.Equal(expected, McpTool.BelongsTo(toolName, server));
    }
}
