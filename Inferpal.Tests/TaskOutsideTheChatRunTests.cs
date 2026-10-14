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
using Inferpal.Services.Tasks;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A <c>/task</c> runs while a chat turn is open, through the chat's own tools — and its calls entered the CHAT's run:
/// a file the task read counted as read by the chat's model, so <c>write_file</c>'s "not read in this run" guard let
/// the chat rewrite a file it had never read; the turn's result bar counted the task's reads, and /replay listed them.
/// </summary>
public sealed class TaskOutsideTheChatRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"taskrun-{Guid.NewGuid():N}");
    private readonly ProjectIndexService _index;
    private readonly ToolRegistry _chat;

    public TaskOutsideTheChatRunTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Cart.cs"), "class Cart { }\n");
        var config   = new InferpalConfig();
        var client   = new FakeInferenceProvider();
        var editor   = new NullEditorSurface();
        var approval = new NoopApproval();
        _index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _index.SetRoot(_root);
        _chat = new ToolRegistry(editor, approval, config, _index, client, new ProjectMapService(editor),
                                 new McpToolService(config, approval), new DocsIndexService(client, config),
                                 new OpenDocumentOverlay(), new NullDebugSession());
    }

    public void Dispose()
    {
        _index.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static JsonElement ReadCart(string root) =>
        JsonSerializer.SerializeToElement(new { path = Path.Combine(root, "Cart.cs") });

    [Fact]
    public async Task AFileTheTaskRead_IsNotReadInTheChatsRun()
    {
        _chat.History.BeginRun();   // the open chat turn

        await new BackgroundTaskToolRegistry(_chat).ExecuteAsync("read_file", ReadCart(_root), CancellationToken.None);

        Assert.NotEqual(true, _chat.History.WasRead(Path.Combine(_root, "Cart.cs")));
        Assert.Equal(0, _chat.History.Runs[^1].FilesOpened);
    }

    [Fact]
    public async Task AFileTheChatRead_IsReadInItsRun()
    {
        // Reference arm: the chat's own read is what the guard counts.
        _chat.History.BeginRun();

        await _chat.ExecuteAsync("read_file", ReadCart(_root), CancellationToken.None);

        Assert.True(_chat.History.WasRead(Path.Combine(_root, "Cart.cs")));
    }
}
