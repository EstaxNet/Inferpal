using Inferpal.Services.Editor;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Services.VsIntegration;

/// <summary>
/// Visual Studio implementation of <see cref="IEditorSurface"/>: reads the active text view
/// through the Extensibility SDK and the open-tab set tracked by <see cref="VsContextHolder"/>.
/// </summary>
internal sealed class VsEditorSurface : IEditorSurface
{
    private readonly VisualStudioExtensibility _vs;
    private readonly VsContextHolder           _contextHolder;

    public VsEditorSurface(VisualStudioExtensibility vs, VsContextHolder contextHolder)
    {
        _vs            = vs;
        _contextHolder = contextHolder;
    }

    public bool IsAvailable => _contextHolder.Context is not null;

    public string? ActiveDocumentPath => _contextHolder.LatestView?.Document.Uri.LocalPath;

    public IReadOnlyList<string> GetOpenDocumentPaths() => _contextHolder.GetOpenPaths();

    public async Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct)
    {
        var view = await GetActiveViewAsync(ct);
        return view is null
            ? null
            : new ActiveDocument(view.Document.Uri.LocalPath, view.Document.Text.CopyToString());
    }

    public async Task<string?> InsertAtCursorAsync(string text, CancellationToken ct)
    {
        var view = await GetActiveViewAsync(ct);
        if (view is null) return null;

        var insertionPoint = view.Selection.InsertionPosition;
        var path           = view.FilePath ?? view.Document.Uri.LocalPath;

        await _vs.Editor().EditAsync(
            batch => view.Document.AsEditable(batch).Insert(insertionPoint, text),
            ct);

        return path;
    }

    public async Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct)
    {
        var view = await GetActiveViewAsync(ct);
        if (view is null) return null;

        var selection = view.Selection;
        var path      = view.FilePath ?? view.Document.Uri.LocalPath;

        await _vs.Editor().EditAsync(
            batch =>
            {
                var docEditor = view.Document.AsEditable(batch);
                if (selection.IsEmpty)
                    docEditor.Insert(selection.InsertionPosition, text);
                else
                    docEditor.Replace(new TextRange(selection.Start, selection.End), text);
            },
            ct);

        return new EditorEditResult(path, ReplacedSelection: !selection.IsEmpty);
    }

    // VS has no cheap cross-language diagnostics query in the out-of-proc SDK; the tool's
    // dotnet-build flow (plus the VsBuildMonitor signals) covers diagnostics in VS.
    public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    // Null when the extension has not yet received a client context (no editor activated
    // since startup) or when no text view currently has focus.
    private async Task<ITextViewSnapshot?> GetActiveViewAsync(CancellationToken ct)
    {
        if (_contextHolder.Context is null) return null;
        return await _vs.Editor().GetActiveTextViewAsync(_contextHolder.Context, ct);
    }
}
