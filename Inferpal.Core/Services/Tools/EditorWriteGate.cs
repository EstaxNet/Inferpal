using Inferpal.Localization;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Tools;

/// <summary>
/// What <c>insert_at_cursor</c> and <c>replace_selection</c> must do before they touch the
/// document the user is looking at: ask, and keep a copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> These two tools mutate a file. They went through
/// none of what every other mutating tool goes through: no approval prompt, no permission rules,
/// no snapshot — the constructor did not even take an <see cref="IApprovalService"/>. The rest of
/// the code base already disagreed with that: <c>PlanModeToolRegistry</c>, whose allow-list is the
/// repository's own definition of "read-only", excludes both.
/// </para>
/// <para>
/// What was actually bypassed is worth naming, because "the user can see it happen and press
/// Ctrl+Z" covers only the accident, not the rest:
/// </para>
/// <list type="bullet">
///   <item><b>Permission rules.</b> A repository shipping <c>deny * \.env$</c> stopped
///         <c>write_file</c> and did nothing about the same file open in the editor.</item>
///   <item><b>The force-prompt on repository-authored input.</b> Content the repository wrote
///         must never ride a consent the user gave their own agent — that guarantee was simply
///         absent here.</item>
///   <item><b>The history.</b> <c>/undo-run</c> restores what a run changed; a run that edited
///         through the caret left nothing to restore.</item>
/// </list>
/// <para>
/// The subject handed to the rules is the <b>path</b>, like every other file tool, so a rule
/// written for a file means the same thing whichever tool reaches it.
/// </para>
/// </remarks>
internal static class EditorWriteGate
{
    /// <summary>
    /// Outcome of the gate: the document to edit and the text to write into it, or the message to return instead.
    /// </summary>
    /// <param name="Text">
    /// The text in the document's own line endings. ⚠ Model output is LF and Visual Studio's editor inserts a text
    /// exactly as given: a CRLF document (its default on Windows) otherwise gets LF lines in the middle — the mixed
    /// endings <c>apply_diff</c>, <c>write_file</c> and the code actions convert away. A document without a line
    /// break says nothing about its convention: the text is kept as given.
    /// </param>
    internal readonly record struct Decision(ActiveDocument? Document, string? Refusal, string? Text = null)
    {
        internal bool MayProceed => Document is not null;
    }

    /// <summary>
    /// Resolves the active document, asks for approval on its path, and snapshots it. The caller
    /// applies the edit only when <see cref="Decision.MayProceed"/> is true.
    /// </summary>
    /// <param name="toolName">Tool name, as the rules and the prompt will show it.</param>
    /// <param name="text">The text about to be written, shown in the prompt.</param>
    /// <summary>What to say when the editor names no active document.</summary>
    /// <remarks>⚠ "No file open" is false while files ARE open and none has the focus (Visual Studio after the active file
    /// is closed, VS Code with the focus in a panel): the model then asks the user to open a file that is on screen.</remarks>
    internal static string NoActiveDocument(IEditorSurface editor) =>
        editor.GetOpenDocumentPaths().Count > 0 ? Strings.ActiveDocNoFocus : Strings.ActiveDocNoFile;

    internal static async Task<Decision> AuthorizeAsync(
        IEditorSurface editor, IApprovalService approval, FileHistoryService history,
        string toolName, string text, CancellationToken ct)
    {
        if (!editor.IsAvailable) return new(null, Strings.ActiveDocNoContext);

        var document = await editor.GetActiveDocumentAsync(ct);
        if (document is null || string.IsNullOrEmpty(document.Path))
            return new(null, NoActiveDocument(editor));

        if (!await approval.RequestApprovalAsync(toolName, $"{document.Path}\n\n{text}", ct, subject: document.Path))
            return new(null, Strings.RunCancelled);

        // Best-effort, and deliberately of the file on disk: an unsaved buffer's previous state
        // lives in the editor's own undo stack, which is where the user looks for it. What the
        // snapshot buys is /undo-run, which reads the disk copy.
        await history.SnapshotAsync(document.Path, ct);
        return new(document, null,
            document.Text.Contains('\n') ? LineEndings.ToEol(text, LineEndings.Dominant(document.Text)) : text);
    }
}
