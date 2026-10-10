using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Editor;
using Inferpal.Services.Mcp;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the Core gained for an editor that tells the agent almost nothing (an ACP client): unsaved buffers asked about
/// one file at a time, the files a call names, and the MCP servers the editor hands a session.
/// </summary>
public sealed class AcpCorePiecesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"acp-core-{Guid.NewGuid():N}");

    public AcpCorePiecesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task AProbedOverlay_CallsAFileUnsaved_OnlyWhenTheEditorsTextDiffersFromTheDisk()
    {
        var typed = Path.Combine(_dir, "typed.txt");
        var clean = Path.Combine(_dir, "clean.txt");
        File.WriteAllText(typed, "line\n");
        File.WriteAllText(clean, "a\r\nb\r\n");
        var asked = new List<string>();
        var overlay = new OpenDocumentOverlay((path, _) =>
        {
            asked.Add(Path.GetFileName(path));
            // The editor hands the clean file back with LF: the same text, not unsaved changes.
            return Task.FromResult<string?>(path == typed ? "line\nmore typing\n" : "a\nb\n");
        });

        await overlay.RefreshAsync(CancellationToken.None, [typed, clean]);

        Assert.True(overlay.HasUnsavedChanges(typed));
        Assert.True(overlay.TryGetUnsaved(typed, out var buffer));
        Assert.Equal("line\nmore typing\n", buffer);
        Assert.False(overlay.HasUnsavedChanges(clean));
        Assert.Equal(["typed.txt", "clean.txt"], asked);
    }

    [Fact]
    public async Task AProbedOverlay_AsksNothing_WithoutPaths_AndKeepsWhatItKnew_WhenTheEditorCannotSay()
    {
        var file = Path.Combine(_dir, "f.txt");
        File.WriteAllText(file, "disk\n");
        string? answer = "buffer\n";
        var calls = 0;
        var overlay = new OpenDocumentOverlay((_, _) => { calls++; return Task.FromResult<string?>(answer); });

        await overlay.RefreshAsync(CancellationToken.None);
        Assert.Equal(0, calls);

        await overlay.RefreshAsync(CancellationToken.None, [file]);
        answer = null;   // the editor can no longer say (a file outside its project)
        await overlay.RefreshAsync(CancellationToken.None, [file]);
        Assert.True(overlay.HasUnsavedChanges(file));
    }

    [Fact]
    public void TheFilesACallNames_AreFoundWhereverTheyAre_AndOnlyWhenTheyExist()
    {
        var a = Path.Combine(_dir, "a.cs");
        var b = Path.Combine(_dir, "sub", "b.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.WriteAllText(a, "");
        File.WriteAllText(b, "");
        var args = JsonSerializer.SerializeToElement(new
        {
            edits = new[] { new { path = "a.cs", old_content = "x" }, new { path = "sub/b.cs", old_content = "y" } },
            note = "not a file\nat all",
            missing = "nowhere.cs",
        });

        var named = ToolRegistry.NamedFiles(args, _dir);

        Assert.Equal([Path.GetFullPath(a), Path.GetFullPath(b)], named);
    }

    private sealed class Approve : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
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

    [Fact]
    public async Task TheEditorsServers_StartEvenWithInferpalsMcpSwitchOff()
    {
        var config = new InferpalConfig { McpEnabled = false };
        await using var mcp = new McpToolService(config, new Approve(), c => new FakeClient(c.Name),
                                                 tokenStore: new McpTokenStore(Path.Combine(_dir, "tokens.json")))
        {
            SessionServers = [new McpServerConfig("from-editor", "node", [], new Dictionary<string, string>())],
        };

        await mcp.RefreshAsync();

        var status = Assert.Single(mcp.Status);
        Assert.Equal("from-editor", status.Name);
        Assert.True(status.Connected);
    }

    [Fact]
    public async Task AnEditorsServer_NamedLikeTheUsersOwn_IsSaidNotStarted_AndTheUsersRuns()
    {
        var config = new InferpalConfig
        {
            McpEnabled = true,
            McpServersJson = """{ "shared": { "command": "node" } }""",
        };
        var started = new List<string>();
        var startedCommands = new List<string?>();
        await using var mcp = new McpToolService(config, new Approve(), c =>
                                                 {
                                                     lock (started) { started.Add(c.Name); startedCommands.Add(c.Command); }
                                                     return new FakeClient(c.Name);
                                                 },
                                                 tokenStore: new McpTokenStore(Path.Combine(_dir, "tokens.json")),
                                                 sharedServersJson: () => config.McpServersJson)
        {
            SessionServers = [new McpServerConfig("shared", "python", [], new Dictionary<string, string>())],
        };

        await mcp.RefreshAsync();

        // Every start is the user's server (the constructor's own refresh included), never the editor's of that name.
        Assert.NotEmpty(started);
        Assert.All(started, name => Assert.Equal("shared", name));
        Assert.Single(mcp.Status, s => s.Name == "shared" && s.Connected);
        Assert.Equal("node", startedCommands.Distinct().Single());
        Assert.Contains(mcp.Status, s => s.Name == "shared" && !s.Connected && s.Error!.Contains("editor", StringComparison.Ordinal));
    }
}
