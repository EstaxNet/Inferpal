namespace Inferpal.Services.Editor;

/// <summary>Editor-agnostic snapshot of the document that currently has focus.</summary>
internal sealed record ActiveDocument(string Path, string Text);

/// <summary>Result of a caret/selection edit: the file touched and whether a selection was replaced.</summary>
internal sealed record EditorEditResult(string Path, bool ReplacedSelection);

/// <summary>
/// Port abstracting the host editor for tools and services, so the logic layer never
/// references an editor SDK directly. Implemented per editor (VS: <c>VsEditorSurface</c>).
/// </summary>
/// <remarks>
/// Every member is a best-effort view: <c>null</c> (or an empty list) means "no active
/// document / editor state unavailable", never an error. Implementations must be safe to
/// call from any thread.
/// </remarks>
internal interface IEditorSurface
{
    /// <summary>
    /// False while the editor has not yet handed the extension a usable context (VS: no
    /// <c>IClientContext</c> captured since startup). Distinct from "no active document":
    /// tools use it to tell the user how to recover instead of claiming no file is open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Who has to check it, measured rather than judged.</b> The line is
    /// <i>assertion</i> versus <i>hint</i>, not "is this a tool":
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Assertion</b> — the emptiness reaches the model as a statement about the user's
    ///   editor. These must check: <c>get_active_document</c>, <c>get_open_editors</c>, and
    ///   <c>EditorWriteGate</c> (behind <c>insert_at_cursor</c>/<c>replace_selection</c>).
    ///   <c>get_open_editors</c> was the one that did not, and answered "No files are currently
    ///   open in the editor" whenever the surface itself was what was missing — so two tools gave
    ///   opposite answers about the same session, and the model cannot prefer the true one.</item>
    ///   <item><b>Hint</b> — the open paths seed a lookup that falls back elsewhere when they are
    ///   empty: <c>get_git_status</c> and <c>get_solution_info</c> (finding the root),
    ///   <c>update_memory</c> (finding the project), <c>ProjectMapService</c>. These assert
    ///   nothing and need no check; an empty hint simply costs them a shortcut.</item>
    /// </list>
    /// <para>
    /// Deliberately not enforced by a scan: the discriminator above is semantic, so a rule would
    /// live on its own exemption list — the antipattern <c>ConventionCoverageTests</c> exists to
    /// replace. Held by <c>EditorSurfaceAvailabilityTests</c> instead, including the cross-tool
    /// property (the two editor tools never disagree about the same session).
    /// </para>
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>Path of the active document, or <c>null</c> when none has focus.</summary>
    string? ActiveDocumentPath { get; }

    /// <summary>Paths of the documents currently open in the editor.</summary>
    IReadOnlyList<string> GetOpenDocumentPaths();

    /// <summary>Full snapshot (path + text) of the active document, or <c>null</c> when none.</summary>
    Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct);

    /// <summary>
    /// Inserts <paramref name="text"/> at the caret of the active document.
    /// Returns the file path, or <c>null</c> when no editor is active.
    /// </summary>
    Task<string?> InsertAtCursorAsync(string text, CancellationToken ct);

    /// <summary>
    /// Replaces the current selection with <paramref name="text"/> (inserts at the caret when
    /// the selection is empty). Returns <c>null</c> when no editor is active.
    /// </summary>
    Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct);

    /// <summary>
    /// Live diagnostics from the editor's language services (Problems panel), pre-formatted
    /// one per line, or <c>null</c> when the editor has none to offer — either the surface
    /// doesn't expose them (VS: the build-based flow is used instead) or the panel is clean.
    /// Callers treat <c>null</c> as "fall back to compiling".
    /// </summary>
    Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct);
}
