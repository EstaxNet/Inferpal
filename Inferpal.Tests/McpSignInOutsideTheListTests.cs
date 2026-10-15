using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Execution;
using Inferpal.Services.Mcp;
using Inferpal.Services.Mcp.OAuth;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An HTTP server the repository declares, or the editor passes, that asks for a sign-in has a card — and the card's
/// sign-in reaches it.
/// </summary>
/// <remarks>
/// Such a server refuses the session until it has a sign-in, so it never runs. The sign-in looked for it among the
/// user's list and the RUNNING servers only, and threw "not a configured HTTP MCP server"; and neither editor drew a card
/// for a server outside the user's list. It could never be signed in to, in either editor or in an ACP client.
/// </remarks>
public sealed class McpSignInOutsideTheListTests : IDisposable
{
    private readonly string _repo  = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"mcp-signin-{Guid.NewGuid():N}");
    private readonly string _state = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"mcp-signin-state-{Guid.NewGuid():N}");

    public McpSignInOutsideTheListTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(_state);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* best-effort cleanup */ }
        try { Directory.Delete(_state, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class Agree(InferpalConfig config) : ApprovalServiceBase(config, () => null)
    {
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct) =>
            Task.FromResult(ApprovalDecision.Once);
    }

    /// <summary>An HTTP server answers 401 until signed in; a stdio server starts.</summary>
    private sealed class SignInClient(McpServerConfig config) : IMcpClient
    {
        public string ServerName => config.Name;
        public string? LastError => config.IsHttp ? "HTTP 401" : null;
        public bool NeedsAuthorization => config.IsHttp;
        public Task<bool> StartAsync(CancellationToken ct) => Task.FromResult(!config.IsHttp);
        public Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>?>([]);
        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct) => Task.FromResult("ok");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public event Action? ToolsChanged { add { } remove { } }
        public event Action? Closed { add { } remove { } }
    }

    private McpToolService Service(InferpalConfig config) =>
        new(config, new Agree(config), c => new SignInClient(c),
            tokenStore: new McpTokenStore(Path.Combine(_state, "tokens.bin")),
            sharedServersJson: () => null, workspaceRoot: () => _repo)
        {
            Consents = new RepoMcpConsents { PathOverride = Path.Combine(_state, "consents.json") },
        };

    [Fact]
    public async Task ARepositorysServerThatAsksForASignIn_HasACard_AndItsSignInReachesIt()
    {
        File.WriteAllText(Path.Combine(_repo, ".mcp.json"),
            """{ "mcpServers": { "api": { "url": "https://mcp.example.com/mcp" } } }""");
        var config = new InferpalConfig { McpEnabled = false };
        await using var service = Service(config);
        config.McpEnabled = true;

        await service.RefreshAsync();

        Assert.Equal("https://mcp.example.com/mcp", service.SignInTarget("api")?.Url);
        var card = Assert.Single(McpServerCards.Build([], service.Status, service.OtherServers));
        Assert.True(card.OutsideTheList);
        Assert.Equal(McpCardState.NeedsSignIn, card.State);
        Assert.Equal(("HTTP", "https://mcp.example.com/mcp"), (card.Transport, card.Target));
    }

    [Fact]
    public async Task AServerTheEditorPasses_ThatAsksForASignIn_HasACard_AndItsSignInReachesIt()
    {
        var config = new InferpalConfig { McpEnabled = false };
        await using var service = Service(config);
        service.SessionServers = [new McpServerConfig("remote", null, [], new Dictionary<string, string>(), Url: "https://tools.example.com/mcp")];

        await service.RefreshAsync();

        Assert.Equal("https://tools.example.com/mcp", service.SignInTarget("remote")?.Url);
        var card = Assert.Single(McpServerCards.Build([], service.Status, service.OtherServers));
        Assert.True(card.OutsideTheList);
        Assert.Equal(McpCardState.NeedsSignIn, card.State);
    }

    [Fact]
    public async Task TheUsersOwnServers_KeepTheirEditableCards_AndANameNoServerHas_IsNoTarget()
    {
        // Reference arm: the user's list is drawn as before, and a sign-in to nothing is still refused.
        var config = new InferpalConfig
        {
            McpEnabled = false,
            McpServersJson = """{ "mine": { "url": "https://mine.example.com/mcp" }, "fs": { "command": "npx" } }""",
        };
        await using var service = Service(config);
        config.McpEnabled = true;

        await service.RefreshAsync();

        var cards = McpServerCards.Build(McpServerConfig.Parse(config.McpServersJson), service.Status, service.OtherServers);
        Assert.Equal(["mine", "fs"], cards.Select(c => c.Name));
        Assert.All(cards, c => Assert.False(c.OutsideTheList));
        Assert.NotNull(service.SignInTarget("mine"));
        Assert.Null(service.SignInTarget("nobody"));
        Assert.Null(service.SignInTarget("fs"));                               // stdio: nothing to sign in to
    }

    [Fact]
    public void BothEditors_DrawTheCardsOutsideTheList_WithTheirSignIn()
    {
        var root = ConventionCoverageTests.RepoRoot();
        // VS Code: the webview draws the cards the host marks, and their button signs in.
        var webview = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(root, "vscode", "src", "webview", "settingsEditors.ts")));
        Assert.Contains("c.outsideTheList", webview, StringComparison.Ordinal);
        Assert.Contains("{ type: 'mcpAuthorize', name: other.name }", webview, StringComparison.Ordinal);
        Assert.Contains("c.OutsideTheList", ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Host", "HostSettingsEditors.cs")),
                        StringComparison.Ordinal);
        // Visual Studio: the settings window builds its rows from the same cards, the others included.
        var vs = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));
        Assert.Contains("_mcp.Status, _mcp.OtherServers)", vs, StringComparison.Ordinal);
        Assert.Contains("RefreshOtherServerRows(built.Where(c => c.OutsideTheList)", vs, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding OtherMcpServers}\"",
                        File.ReadAllText(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsContent.xaml")), StringComparison.Ordinal);
    }
}
