using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An edit that did not go through was announced as "no file open in the editor" — by a tool whose
/// own gate had just established the opposite.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Two contradictory statements about the same session, in the same call.</b>
/// <c>EditorWriteGate</c> resolves the active document and <b>refuses</b> when there is none: past
/// that point, a file <i>is</i> open. The <c>null</c> the surface returned afterwards could
/// therefore no longer mean "no file" — it meant "the edit did not apply" — and both tools returned
/// <c>Strings.ActiveDocNoFile</c> anyway. The model then reads that nothing is open, calls
/// <c>get_active_document</c>, and is given the file's path: exactly the contradiction
/// <c>EditorSurfaceAvailabilityTests</c> exists to forbid, reached through another door.
/// </para>
/// <para>
/// ⚠ <b>And the wrong cause sends the reader to the wrong place</b>: "open a file" is not the remedy
/// when the document changed under the edit or is read-only. On the VS Code side that is the real
/// case — <c>editor.edit()</c> returns <c>false</c>, or the editor was disposed between two focus
/// changes. On the VS side the edit <b>throws</b>, so the error surfaced as it was: the two
/// front-ends did not even get it wrong in the same way.
/// </para>
/// </remarks>
[Collection(WorkingDirectoryCollection.Name)]
public class EditThatDidNotApplyTests
{
    /// <summary>A genuinely active document whose edit does not go through.</summary>
    private sealed class EditRefusingEditor : IEditorSurface
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inferpal-tests-refusing.cs");

        public bool IsAvailable => true;
        public string? ActiveDocumentPath => Path;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [Path];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<ActiveDocument?>(new ActiveDocument(Path, "before"));

        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
            Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>The reference arm: here there really is no file.</summary>
    private sealed class NoDocumentEditor : IEditorSurface
    {
        public bool IsAvailable => true;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
            Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class Approves : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, Services.CodeActions.DiffInfo? diff = null,
                                               bool forcePrompt = false) => Task.FromResult(true);
    }

    private static JsonElement Args(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    // ── The defect ───────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertThatDoesNotApply_IsNotReportedAsNoFileOpen()
    {
        var editor = new EditRefusingEditor();

        // WITNESS, and the contradiction itself: on THIS session, the tool that describes the
        // editor names the file.
        var described = await new GetActiveDocumentTool(editor).ExecuteAsync(Args(new { }), CancellationToken.None);
        Assert.Contains(editor.Path, described, StringComparison.Ordinal);

        var said = await new InsertAtCursorTool(editor, new Approves(), new FileHistoryService())
            .ExecuteAsync(Args(new { text = "hello" }), CancellationToken.None);

        Assert.NotEqual(Strings.ActiveDocNoFile, said);
        Assert.Contains(editor.Path, said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceThatDoesNotApply_IsNotReportedAsNoFileOpen()
    {
        var editor = new EditRefusingEditor();

        var said = await new ReplaceSelectionTool(editor, new Approves(), new FileHistoryService())
            .ExecuteAsync(Args(new { text = "hello" }), CancellationToken.None);

        Assert.NotEqual(Strings.ActiveDocNoFile, said);
        Assert.Contains(editor.Path, said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AndItDoesNotReadLikeASuccessEither()
    {
        // ⚠ The other half: do not say "written". The model would carry on against a file it
        // believes changed — the costlier of the two failures.
        var editor = new EditRefusingEditor();

        var said = await new InsertAtCursorTool(editor, new Approves(), new FileHistoryService())
            .ExecuteAsync(Args(new { text = "hello" }), CancellationToken.None);

        Assert.NotEqual(Strings.InsertOk(editor.Path, 5), said);
    }

    // ── The reference arms ───────────────────────────────────────────────────

    [Fact]
    public async Task WithNoDocumentAtAll_BothToolsStillSayTheSameThing()
    {
        // Without it, a tool that NEVER said "no file" would sail through the tests above while
        // proving nothing — and that sentence stays the right one when it is true.
        var editor = new NoDocumentEditor();

        var described = await new GetActiveDocumentTool(editor).ExecuteAsync(Args(new { }), CancellationToken.None);
        var inserted  = await new InsertAtCursorTool(editor, new Approves(), new FileHistoryService())
            .ExecuteAsync(Args(new { text = "hello" }), CancellationToken.None);
        var replaced  = await new ReplaceSelectionTool(editor, new Approves(), new FileHistoryService())
            .ExecuteAsync(Args(new { text = "hello" }), CancellationToken.None);

        Assert.Equal(Strings.ActiveDocNoFile, described);
        Assert.Equal(Strings.ActiveDocNoFile, inserted);
        Assert.Equal(Strings.ActiveDocNoFile, replaced);
    }

    [Fact]
    public async Task AnEditThatAppliesStillSaysSo()
    {
        // The other arm: the nominal path has not moved.
        var editor = new AppliesEditor();

        var said = await new InsertAtCursorTool(editor, new Approves(), new FileHistoryService())
            .ExecuteAsync(Args(new { text = "hello" }), CancellationToken.None);

        Assert.Equal(Strings.InsertOk(editor.Path, 5), said);
    }

    private sealed class AppliesEditor : IEditorSurface
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inferpal-tests-applies.cs");

        public bool IsAvailable => true;
        public string? ActiveDocumentPath => Path;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [Path];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<ActiveDocument?>(new ActiveDocument(Path, "before"));
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) =>
            Task.FromResult<string?>(Path);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
            Task.FromResult<EditorEditResult?>(new EditorEditResult(Path, ReplacedSelection: true));
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
