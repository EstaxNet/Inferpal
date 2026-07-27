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
