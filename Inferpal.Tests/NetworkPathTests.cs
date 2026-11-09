using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A path the model writes that names another machine is never asked of the disk — asking IS a connection to that host
/// (SMB, about 21 s against one that does not answer) — and a built-in tool refuses it before anything else. Windows only:
/// elsewhere <c>//x</c> is a local path.
/// </summary>
public class NetworkPathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("netpath-").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Theory]
    [InlineData(@"\\host\share", true)]
    [InlineData(@"\\host\share\a.txt", true)]
    [InlineData(@"//host/share/a.txt", true)]
    [InlineData(@"/\host\share", true)]
    [InlineData(@"\\?\UNC\host\share\a", true)]
    [InlineData(@"\\.\UNC\host\share\a", true)]
    [InlineData(@"\??\UNC\host\share\a", true)]
    [InlineData(@"\\?\GLOBALROOT\Device\Mup\host\share", true)]
    [InlineData(@"\\.\GLOBALROOT\Device\Mup\host\share", true)]
    [InlineData(@"\??\GLOBALROOT\Device\Mup\host\share", true)]
    // This machine's own shares reach the workspace's files: not the network, the exclusion rules keep judging them.
    [InlineData(@"\\localhost\C$\work\a.txt", false)]
    [InlineData(@"\\?\UNC\localhost\C$\work\a.txt", false)]
    [InlineData(@"\\127.0.0.1\c$\work\a.txt", false)]
    [InlineData(@"\\.\share\a.txt", false)]
    [InlineData(@"\\?\C:\work\a.txt", false)]
    [InlineData(@"\\.\C:", false)]
    [InlineData(@"\\.\pipe\name", false)]
    [InlineData(@"C:\work\a.txt", false)]
    [InlineData(@"src\a.txt", false)]
    [InlineData(@"\\\triple", false)]
    [InlineData(@"https://example.com/a", false)]
    public void TheClassifier_ReadsTheTextAlone(string path, bool network)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(network, NetworkPath.Is(path));
    }

    [Fact]
    public void AWorkspaceOnAShare_KeepsItsOwnFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(NetworkPath.OutsideTheWorkspace(@"\\host\share\repo\src\a.cs", @"\\host\share\repo"));
        Assert.False(NetworkPath.OutsideTheWorkspace(@"\\HOST\Share\Repo", @"\\host\share\repo\"));
        Assert.True(NetworkPath.OutsideTheWorkspace(@"\\host\share\other\a.cs", @"\\host\share\repo"));
        Assert.True(NetworkPath.OutsideTheWorkspace(@"\\host\share\repo2\a.cs", @"\\host\share\repo"));
        Assert.True(NetworkPath.OutsideTheWorkspace(@"\\host\share\a.cs", @"C:\work"));
        // The same share in its long-path spelling is the same workspace.
        Assert.False(NetworkPath.OutsideTheWorkspace(@"\\host\share\repo\src\a.cs", @"\\?\UNC\host\share\repo"));
        Assert.False(NetworkPath.OutsideTheWorkspace(@"\\?\UNC\host\share\repo\a.cs", @"\\host\share\repo"));
        Assert.False(NetworkPath.Is($@"\\{Environment.MachineName}\c$\work\a.txt"));
    }

    /// <summary>⚠ Every string is walked, text arguments included (a command, a pattern): none that names another machine
    /// reaches the disk. Reference arm: a local path IS asked about — the witness is alive.</summary>
    [Fact]
    public void ThePathReaders_NeverAskTheDiskAboutAnotherMachine()
    {
        if (!OperatingSystem.IsWindows()) return;
        var asked = new List<string>();
        ToolRegistry.DiskProbeWitness.Value = asked.Add;
        try
        {
            var unc = JsonSerializer.SerializeToElement(new
            {
                path = @"\\192.0.2.250\share\a.txt",
                command = @"type \\192.0.2.251\share\b.txt",
                pattern = @"//192.0.2.252/share",
                edits = new[] { new { path = @"\\?\UNC\192.0.2.253\share\c.cs" } },
            });
            Assert.Empty(ToolRegistry.NamedFiles(unc, _root));
            Assert.Null(ToolRegistry.ExcludedNamedFile(unc, _root));
            // A text that only resolves under the root ("type \\h\s" joined to it) is a local path: asking about it is fine.
            Assert.DoesNotContain(asked, NetworkPath.Is);

            ToolRegistry.NamedFiles(JsonSerializer.SerializeToElement(new { path = "src/a.cs" }), _root);
            Assert.Contains(asked, p => p.EndsWith(Path.Combine("src", "a.cs"), StringComparison.OrdinalIgnoreCase));
        }
        finally { ToolRegistry.DiskProbeWitness.Value = null; }
    }

    private ToolRegistry Registry(string customTools = "")
    {
        var config = new InferpalConfig { CustomTools = customTools };
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        index.SetRoot(_root);
        return new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                new McpToolService(config, approval), new DocsIndexService(client, config),
                                new OpenDocumentOverlay(), new NullDebugSession());
    }

    [Fact]
    public async Task ABuiltInTool_RefusesAPathOnAnotherMachine_BeforeAnythingAsksTheDisk()
    {
        if (!OperatingSystem.IsWindows()) return;
        var asked = new List<string>();
        ToolRegistry.DiskProbeWitness.Value = asked.Add;
        try
        {
            var result = await Registry().ExecuteAsync("search_in_files",
                JsonSerializer.SerializeToElement(new { query = "x", path = @"\\192.0.2.254\share" }), default);

            Assert.Equal(ToolRegistry.NetworkRefusal(@"\\192.0.2.254\share"), result);
            Assert.Empty(asked);
        }
        finally { ToolRegistry.DiskProbeWitness.Value = null; }
    }

    /// <summary><c>restore_file</c>'s <c>snapshot</c> is a path it resolves: refused like any other, never reached.</summary>
    [Fact]
    public async Task ASnapshotOnAnotherMachine_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await Registry().ExecuteAsync("restore_file",
            JsonSerializer.SerializeToElement(new { path = "a.cs", snapshot = @"\\192.0.2.248\share\x" }), default);

        Assert.Equal(ToolRegistry.NetworkRefusal(@"\\192.0.2.248\share\x"), result);
    }

    /// <summary>The person's own tool is not refused: its card decides (an MCP server whose root is a share keeps working).
    /// The card is counted and declined, so nothing runs. Reference arm of the refusal above.</summary>
    [Fact]
    public async Task ThePersonsOwnTool_IsNotRefused_ItsCardDecides()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cards = new CountingDecline();
        var config = new InferpalConfig { CustomTools = "list_share=dir" };
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        index.SetRoot(_root);
        var tools = new ToolRegistry(editor, cards, config, index, client, new ProjectMapService(editor),
                                     new McpToolService(config, cards), new DocsIndexService(client, config),
                                     new OpenDocumentOverlay(), new NullDebugSession());

        var result = await tools.ExecuteAsync("list_share",
            JsonSerializer.SerializeToElement(new { path = @"\\192.0.2.249\share" }), default);

        Assert.Equal(1, cards.Asked);
        Assert.DoesNotContain("another machine", result, StringComparison.Ordinal);
    }

    private sealed class CountingDecline : IApprovalService
    {
        public int Asked;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, Services.CodeActions.DiffInfo? diff = null,
                                               bool forcePrompt = false)
        {
            Interlocked.Increment(ref Asked);
            return Task.FromResult(false);
        }
    }

    [Fact]
    public void AWorkspaceThatLivesOnAShare_IsNotRefusedItsOwnPaths()
    {
        if (!OperatingSystem.IsWindows()) return;
        var args = JsonSerializer.SerializeToElement(new { path = @"src\a.cs" });
        Assert.Null(ToolRegistry.NetworkArgument(args, @"\\host\share\repo"));
        Assert.Equal(@"\\host\share\other\a.cs",
                     ToolRegistry.NetworkArgument(JsonSerializer.SerializeToElement(new { path = @"\\host\share\other\a.cs" }), @"\\host\share\repo"));
        // Text arguments are not paths: a command that mentions a share is the shell's card to judge.
        Assert.Null(ToolRegistry.NetworkArgument(JsonSerializer.SerializeToElement(new { command = @"dir \\host\share" }), _root));
    }
}
