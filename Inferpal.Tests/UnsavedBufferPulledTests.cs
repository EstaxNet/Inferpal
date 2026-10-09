using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Editor;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio does not push its buffers: the overlay asks it, before every tool call and again after an approval,
/// which files hold unsaved changes. A write over one of them is refused — otherwise VS asks "reload?" and yes erases
/// what the user typed.
/// </summary>
public sealed class UnsavedBufferPulledTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"pulled-{Guid.NewGuid():N}");

    public UnsavedBufferPulledTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>What the editor answers, changed by the test between calls.</summary>
    private sealed class Editor
    {
        public volatile IReadOnlyList<UnsavedDocument> Unsaved = [];
        public int Asked;
        public Task<IReadOnlyList<UnsavedDocument>> AnswerAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            return Task.FromResult(Unsaved);
        }
    }

    [Fact]
    public async Task APulledOverlay_HoldsWhatTheEditorSaysNow_AndForgetsAFileSavedSince()
    {
        var editor  = new Editor { Unsaved = [new(Path.Combine(_root, "A.cs"), "typed")] };
        var overlay = new OpenDocumentOverlay(editor.AnswerAsync);

        await overlay.RefreshAsync(CancellationToken.None);
        Assert.True(overlay.HasUnsavedChanges(Path.Combine(_root, "A.cs")));

        // Saved since: a mirror that missed the save would refuse every write to this file from now on.
        editor.Unsaved = [];
        await overlay.RefreshAsync(CancellationToken.None);
        Assert.False(overlay.HasUnsavedChanges(Path.Combine(_root, "A.cs")));
    }

    [Fact]
    public async Task AnUnsavedBufferWhoseTextIsUnknown_IsStillUnsaved_ButNotShownAsEmpty()
    {
        var path    = Path.Combine(_root, "Form1.cs");
        var overlay = new OpenDocumentOverlay(new Editor { Unsaved = [new(path, null)] }.AnswerAsync);
        await overlay.RefreshAsync(CancellationToken.None);

        Assert.True(overlay.HasUnsavedChanges(path));
        Assert.False(overlay.TryGetUnsaved(path, out _));   // read from disk, never as an empty file
    }

    [Fact]
    public async Task AnEditorThatDoesNotAnswer_LeavesTheLastAnswerStanding_AndIsSaid()
    {
        var path    = Path.Combine(_root, "A.cs");
        var hang    = false;
        var overlay = new OpenDocumentOverlay(async ct =>
        {
            if (hang) await Task.Delay(Timeout.Infinite, ct);
            return [new UnsavedDocument(path, "typed")];
        });
        await overlay.RefreshAsync(CancellationToken.None);

        hang = true;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        await overlay.RefreshAsync(CancellationToken.None);

        // The budget is the assertion here: the call returns, and "no answer" is not read as "nothing is unsaved".
        Assert.True(waited.Elapsed < OpenDocumentOverlay.RefreshBudget + TimeSpan.FromSeconds(10));
        Assert.True(overlay.HasUnsavedChanges(path));
        Assert.Contains(Diagnostics.Snapshot(), e => e.Context == "OpenDocumentOverlay"
                                                    && e.Detail.Contains("did not say which files have unsaved changes"));
    }

    [Fact]
    public async Task TheRegistry_AsksBeforeEveryCall_AndAgainAfterTheApproval()
    {
        var path = Path.Combine(_root, "Calc.cs");
        File.WriteAllText(path, "int x = 1;\n");
        var editor   = new Editor();
        var approval = new Approval();
        using var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig(), new LspSemanticProvider());
        index.SetRoot(_root);
        var tools = Registry(index, approval, new OpenDocumentOverlay(editor.AnswerAsync));
        JsonElement Edit(string from, string to) => JsonSerializer.SerializeToElement(new { path, old_content = from, new_content = to });

        // 1. Typed before the call: refused before the card.
        editor.Unsaved = [new(path, "int x = 1;\nint typed;\n")];
        var refused = await tools.ExecuteAsync("apply_diff", Edit("int x = 1;", "int x = 2;"), CancellationToken.None);
        Assert.Contains("unsaved changes", refused);
        Assert.Equal(0, approval.Shown);

        // 2. Reference arm: saved since — the next call asks again and the edit goes through.
        editor.Unsaved = [];
        await tools.ExecuteAsync("apply_diff", Edit("int x = 1;", "int x = 2;"), CancellationToken.None);
        Assert.Equal("int x = 2;\n", File.ReadAllText(path));

        // 3. Typed while the card waits: refused after the "yes", nothing written.
        approval.WhileTheCardWaits = () => editor.Unsaved = [new(path, "int x = 2;\nint typed;\n")];
        var meanwhile = await tools.ExecuteAsync("apply_diff", Edit("int x = 2;", "int x = 3;"), CancellationToken.None);
        Assert.Equal(2, approval.Shown);                                  // witness: the card was shown and accepted
        Assert.Contains("while the approval was pending", meanwhile);
        Assert.Equal("int x = 2;\n", File.ReadAllText(path));
    }

    private sealed class Approval : Services.Execution.IApprovalService
    {
        public int Shown;
        public Action? WhileTheCardWaits;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
        {
            Shown++;
            WhileTheCardWaits?.Invoke();
            return Task.FromResult(true);
        }
    }

    private static Services.Execution.ToolRegistry Registry(ProjectIndexService index, Services.Execution.IApprovalService approval,
                                                            OpenDocumentOverlay overlay)
    {
        var config = new InferpalConfig();
        var client = new FakeInferenceProvider();
        var editor = new NoEditor();
        return new Services.Execution.ToolRegistry(editor, approval, config, index, client,
            new ProjectMapService(editor), new McpToolService(config, approval), new Services.Docs.DocsIndexService(client, config),
            overlay);
    }

    private sealed class NoEditor : IEditorSurface
    {
        public bool IsAvailable => false;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) => Task.FromResult<ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) => Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
