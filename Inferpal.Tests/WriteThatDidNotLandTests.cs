using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Editor;
using Inferpal.Services.Lsp;
using Inferpal.Services.Execution;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A file enters the run as CHANGED only when its write landed: a write refused by the file system, or a batch rolled
/// back, leaves the run as if it had never been attempted.
/// </summary>
/// <remarks>
/// ⚠ A file entered the run when its backup was taken — before the write. On a read-only file (a TFVC or Perforce
/// checkout) the backup succeeded, the write threw, and the file still counted: "edited 1" with Undo on the result bar,
/// no "edits without effect" notice, the plan step marked done, a failed last check blamed on a turn that changed files.
/// The check sits in the registry's funnel, so it holds for every tool that writes.
/// </remarks>
[Collection(SignalCollection.Name)]
public sealed class WriteThatDidNotLandTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"inferpal-notlanded-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort cleanup */ }
        _scratch.Dispose();
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private ToolRegistry Registry()
    {
        var config = new InferpalConfig();
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        var index  = new ProjectIndexService(client, config, new LspSemanticProvider());
        index.SetRoot(_root);
        return new ToolRegistry(editor, approval, config, index, client,
                                new ProjectMapService(editor, index), new McpToolService(config, approval),
                                new DocsIndexService(client, config), new OpenDocumentOverlay(), new NullDebugSession());
    }

    private string File(string name, string content, bool readOnly = false)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, content);
        if (readOnly) System.IO.File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    [Fact]
    public async Task AWriteTheFileSystemRefused_IsNotAChangeOfTheRun()
    {
        var path = File("Locked.cs", "class A { }\n", readOnly: true);
        var tools = Registry();
        tools.History.BeginRun();

        await tools.ExecuteAsync("apply_diff", Args(new { path, old_content = "class A", new_content = "class B" }), default);

        Assert.Equal("class A { }\n", System.IO.File.ReadAllText(path));   // witness: it did not land
        Assert.Equal(0, tools.History.CurrentRunFileCount);
        Assert.Equal(0, tools.WritesInRun);
    }

    [Fact]
    public async Task ABatchRolledBack_IsNotAChangeOfTheRun()
    {
        var open   = File("Open.cs", "class A { }\n");
        var locked = File("Locked.cs", "class C { }\n", readOnly: true);
        var tools  = Registry();
        tools.History.BeginRun();

        await tools.ExecuteAsync("apply_edits", Args(new
        {
            edits = new[]
            {
                new { path = open,   old_content = "class A", new_content = "class B" },
                new { path = locked, old_content = "class C", new_content = "class D" },
            },
        }), default);

        Assert.Equal("class A { }\n", System.IO.File.ReadAllText(open));   // witness: rolled back
        Assert.Equal(0, tools.History.CurrentRunFileCount);
        Assert.Equal(0, tools.WritesInRun);
    }

    /// <summary>Reference arms: a write that lands counts, and a later write of the same file that does not land keeps
    /// the earlier one in the run.</summary>
    [Fact]
    public async Task AWriteThatLands_Counts_AndALaterRefusalKeepsIt()
    {
        var path  = File("Open.cs", "class A { }\n");
        var tools = Registry();
        tools.History.BeginRun();

        await tools.ExecuteAsync("apply_diff", Args(new { path, old_content = "class A", new_content = "class B" }), default);
        Assert.Equal("class B { }\n", System.IO.File.ReadAllText(path));
        Assert.Equal(1, tools.History.CurrentRunFileCount);

        System.IO.File.SetAttributes(path, FileAttributes.ReadOnly);
        await tools.ExecuteAsync("apply_diff", Args(new { path, old_content = "class B", new_content = "class C" }), default);

        Assert.Equal(1, tools.History.CurrentRunFileCount);
        Assert.Equal(1, tools.WritesInRun);
    }
}
