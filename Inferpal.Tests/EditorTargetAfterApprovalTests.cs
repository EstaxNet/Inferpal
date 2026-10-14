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

        public bool IsAvailable => true;
        public string? ActiveDocumentPath => Active;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [Approved, Other];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<ActiveDocument?>(new ActiveDocument(Active, "text\n"));

        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct)
        {
            Writes++;
            return Task.FromResult<string?>(Active);
        }

        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct)
        {
            Writes++;
            return Task.FromResult<EditorEditResult?>(new EditorEditResult(Active, ReplacedSelection: true));
        }

        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>Approves, after <paramref name="meanwhile"/> ran — what the user does while the card waits.</summary>
    private sealed class ApprovalThatWaits(Action meanwhile) : IApprovalService
    {
        public int Prompts { get; private set; }

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
        {
            Prompts++;
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
}
