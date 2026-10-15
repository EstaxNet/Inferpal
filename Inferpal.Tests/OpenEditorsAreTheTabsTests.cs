using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: the open editors the model is told about are the files the TABS show, not every document the editor has
/// loaded.
/// </summary>
/// <remarks>
/// The host answered <c>get_open_editors</c> (and the workspace block's "open files") from its overlay of buffers, which
/// mirrors <c>workspace.textDocuments</c>: a file an <c>@</c>-mention read, a document a language server opened, a
/// closed tab's buffer kept for a while — all "open", and an open file too large to mirror was missing.
/// </remarks>
public partial class HostServerTests
{
    [Fact]
    public async Task TheOpenEditors_AreTheTabsTheEditorReports_NotEveryLoadedDocument()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        var shown  = Path.Combine(h.RootDir, "shown.cs");
        var loaded = Path.Combine(h.RootDir, "loaded.cs");
        Task Call(string method, object args) =>
            h.Client.InvokeWithParameterObjectAsync<object?>(method, args).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await Call("textDocument/didOpen", new { path = shown, text = "class Shown { }" });
        await Call("textDocument/didOpen", new { path = loaded, text = "class Loaded { }" });
        var editor = h.Server.CurrentSession!.Editor;

        // Reference arm: an editor that does not report its tabs keeps the overlay's list.
        Assert.Equal(2, editor.GetOpenDocumentPaths().Count);

        await Call("editor/didChangeTabs", new { paths = new[] { shown } });
        Assert.Equal([shown], editor.GetOpenDocumentPaths());

        // The tab closed: nothing open, though the buffer is still loaded.
        await Call("editor/didChangeTabs", new { paths = Array.Empty<string>() });
        Assert.Empty(editor.GetOpenDocumentPaths());
    }

    [Fact]
    public void TheBridge_ReportsItsTabs_AtAttachAndOnEveryChange_FromTheTabsThemselves()
    {
        var attach = ConversationPersistenceSilenceTests.TsMethodBody(
            Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", "editorBridge.ts"),
            "attach(host: HostClient): void");
        Assert.Contains("host.didChangeTabs(openTabPaths())", attach, StringComparison.Ordinal);
        Assert.Contains("tabGroups.onDidChangeTabs(", attach, StringComparison.Ordinal);
        Assert.Contains("tabGroups.onDidChangeTabGroups(", attach, StringComparison.Ordinal);

        var bridge = WebviewRebuildTests.TsCode("editorBridge.ts");
        var start = bridge.IndexOf("function openTabPaths(", StringComparison.Ordinal);
        Assert.True(start >= 0, "openTabPaths was not found: this test would have measured nothing.");
        var end = bridge.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        Assert.True(end > start, "the function after openTabPaths was not found.");
        var body = bridge[start..end];
        Assert.Contains("vscode.window.tabGroups.all", body, StringComparison.Ordinal);
        Assert.DoesNotContain("textDocuments", body, StringComparison.Ordinal);
    }
}
