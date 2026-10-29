using System.Text.Json;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>insert_at_cursor</c> and <c>replace_selection</c> are approved on the document active when they ask; the card can
/// wait for minutes, and the user keeps working. An edit applied to whatever document is active AFTER the approval
/// lands in a file nobody approved — one the rules may deny, at its own caret or over its own selection.
/// </summary>
public class EditorTargetAfterApprovalTests
{
    private const string Approved = @"C:\ws\Program.cs";
    private const string Other    = @"C:\ws\.env";

    /// <summary>An editor whose active document becomes <see cref="Other"/> once the approval was asked.</summary>
    private sealed class SwitchingEditor : IEditorSurface
    {
        public string Active { get; set; } = Approved;
        public int Writes { get; private set; }
        public string? Selection { get; set; }
        public string? SelectionIn { get; set; }   // the document the selection is read in; the active one by default
        public string? ReplacedFor { get; private set; }

        public Task<EditorSelection?> GetSelectionAsync(CancellationToken ct) =>
            Task.FromResult(Selection is null ? null : new EditorSelection(SelectionIn ?? Active, Selection));

        public bool IsAvailable => true;
        public string? ActiveDocumentPath => Active;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [Approved, Other];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<ActiveDocument?>(new ActiveDocument(Active, "text\n"));

        public Task<string?> InsertAtCursorAsync(string text, string path, CancellationToken ct)
        {
            Writes++;
            return Task.FromResult<string?>(Active);
        }

        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, string path, string? selection, CancellationToken ct)
        {
            ReplacedFor = selection;
            // As the editors do: another selection than the one shown was never approved.
            if (selection is not null && selection != Selection) return Task.FromResult<EditorEditResult?>(null);
            Writes++;
            return Task.FromResult<EditorEditResult?>(new EditorEditResult(Active, ReplacedSelection: true));
        }

        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>Approves, after <paramref name="meanwhile"/> ran — what the user does while the card waits.</summary>
    private sealed class ApprovalThatWaits(Action meanwhile) : IApprovalService
    {
        public int Prompts { get; private set; }
        public string? Details { get; private set; }
        public Services.CodeActions.DiffInfo? Diff { get; private set; }

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
        {
            Prompts++;
            Details = details;
            Diff    = diff;
            meanwhile();
            return Task.FromResult(true);
        }
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task InsertAtCursor_WritesNothing_WhenAnotherDocumentBecameActiveDuringTheApproval()
    {
        var editor   = new SwitchingEditor();
        var approval = new ApprovalThatWaits(() => editor.Active = Other);

        var said = await new InsertAtCursorTool(editor, approval, new FileHistoryService())
            .ExecuteAsync(Args(new { text = "SECRET=1" }), CancellationToken.None);

        Assert.Equal(1, approval.Prompts);   // witness: the card was shown and accepted
        Assert.Equal(0, editor.Writes);
        Assert.Contains("no longer the active document", said);
        Assert.Contains(Approved, said);
    }

    [Fact]
    public async Task ReplaceSelection_WritesNothing_WhenAnotherDocumentBecameActiveDuringTheApproval()
    {
        var editor   = new SwitchingEditor();
        var approval = new ApprovalThatWaits(() => editor.Active = Other);

        var said = await new ReplaceSelectionTool(editor, approval, new FileHistoryService())
            .ExecuteAsync(Args(new { text = "x" }), CancellationToken.None);

        Assert.Equal(0, editor.Writes);
        Assert.Contains("no longer the active document", said);
    }

    [Fact]
    public async Task InsertAtCursor_Writes_WhenTheApprovedDocumentIsStillActive()
    {
        // Reference arm: nobody switched documents.
        var editor = new SwitchingEditor();

        await new InsertAtCursorTool(editor, new ApprovalThatWaits(() => { }), new FileHistoryService())
            .ExecuteAsync(Args(new { text = "hello" }), CancellationToken.None);

        Assert.Equal(1, editor.Writes);
    }

    /// <summary>
    /// ⚠ The card of <c>replace_selection</c> shows what it replaces, and the editor replaces that only: a selection made
    /// while the card waits (Ctrl+A to copy something) was never approved.
    /// </summary>
    [Fact]
    public async Task ReplaceSelection_ShowsTheSelectionItReplaces_AndReplacesThatOneOnly()
    {
        var editor   = new SwitchingEditor { Selection = "return OldTotal(cart);" };
        var approval = new ApprovalThatWaits(() => editor.Selection = "the whole document");

        var said = await new ReplaceSelectionTool(editor, approval, new FileHistoryService())
            .ExecuteAsync(Args(new { text = "return NewTotal(cart);" }), CancellationToken.None);

        // The card: the change as a diff it can open whole, and the new text in full.
        Assert.Equal("return OldTotal(cart);", approval.Diff?.OldText);
        Assert.Equal("return NewTotal(cart);", approval.Diff?.NewText);
        Assert.Contains("NewTotal", approval.Details);
        // The editor is handed the selection shown, refuses the one made meanwhile, and the cause is named.
        Assert.Equal("return OldTotal(cart);", editor.ReplacedFor);
        Assert.Equal(0, editor.Writes);
        Assert.Contains("selection", said);
        Assert.Contains("changed while the approval was pending", said);
    }

    [Fact]
    public async Task ReplaceSelection_ASelectionReadInAnotherDocument_IsNeverShownAsThisOnes()
    {
        // The focus moved between reading the document and reading its selection.
        var editor   = new SwitchingEditor { Selection = "secret", SelectionIn = Other };
        var approval = new ApprovalThatWaits(() => { });

        var said = await new ReplaceSelectionTool(editor, approval, new FileHistoryService())
            .ExecuteAsync(Args(new { text = "x" }), CancellationToken.None);

        Assert.Equal(0, approval.Prompts);
        Assert.Equal(0, editor.Writes);
        Assert.Contains("no longer the active document", said);
    }

    private sealed record EditNote(string Text, string? Path, string? Selection = null);

    /// <summary>VS Code's side of the edit: records the document each edit was approved for.</summary>
    private sealed class EditorThatRecords
    {
        public readonly List<string?> ApprovedFor = [];
        public string? ReplacedFor;

        [StreamJsonRpc.JsonRpcMethod("editor/selection")]
        public object Selection() => new { path = Approved, text = "old" };

        [StreamJsonRpc.JsonRpcMethod("editor/insertAtCursor", UseSingleObjectParameterDeserialization = true)]
        public string? Insert(EditNote note) { ApprovedFor.Add(note.Path); return null; }

        [StreamJsonRpc.JsonRpcMethod("editor/replaceSelection", UseSingleObjectParameterDeserialization = true)]
        public Host.EditResultDto Replace(EditNote note)
        {
            ApprovedFor.Add(note.Path);
            ReplacedFor = note.Selection;
            return new Host.EditResultDto(null, false);
        }
    }

    /// <summary>
    /// ⚠ The host's idea of VS Code's active document lags the editor's (its notifications arrive out of order, the focus
    /// moves while the card waits): the editor is told which document the edit was approved for, and checks it where the
    /// edit is applied.
    /// </summary>
    [Fact]
    public async Task VsCode_IsToldTheApprovedDocument_AndChecksItWhereTheEditIsApplied()
    {
        var (hostSide, editorSide) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        var editor = new EditorThatRecords();
        using var editorRpc = Host.HostRpc.Create(editorSide, editorSide, editor);
        using var hostRpc   = Host.HostRpc.Create(hostSide, hostSide);
        editorRpc.StartListening();
        hostRpc.StartListening();
        var surface = new Host.RpcEditorSurface(hostRpc, new OpenDocumentOverlay());

        await surface.InsertAtCursorAsync("x", Approved, CancellationToken.None);
        await surface.ReplaceSelectionAsync("x", Approved, "old", CancellationToken.None);

        Assert.Equal([Approved, Approved], editor.ApprovedFor);
        Assert.Equal(new EditorSelection(Approved, "old"), await surface.GetSelectionAsync(CancellationToken.None));
        Assert.Equal("old", editor.ReplacedFor);
        var bridge = WebviewRebuildTests.TsCode("editorBridge.ts");
        foreach (var method in new[] { "async insertAtCursor(", "async replaceSelection(" })
            Assert.Contains("!this.isApproved(editor, path)", WebviewRebuildTests.Body(bridge, method), StringComparison.Ordinal);
        Assert.Contains("samePath(editor.document.uri.fsPath, path)",
                        WebviewRebuildTests.Body(bridge, "private isApproved("), StringComparison.Ordinal);
        Assert.Contains("editor.document.getText(editor.selection) !== selection",
                        WebviewRebuildTests.Body(bridge, "async replaceSelection("), StringComparison.Ordinal);
    }
}
