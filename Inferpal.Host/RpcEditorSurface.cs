using Inferpal.Services;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// <see cref="IEditorSurface"/> backed by reverse JSON-RPC requests to the editor adapter
/// (`editor/activeDocument`, `editor/insertAtCursor`, `editor/replaceSelection`).
/// The active-document path is pushed by the adapter (`editor/didChangeActiveDocument`)
/// because the adapter loses <c>activeTextEditor</c> focus to its own webview; the last
/// active editor is the correct answer, so it is cached here.
/// </summary>
/// <remarks>
/// Honors the port's best-effort contract: any RPC failure (adapter gone, method not
/// implemented yet) degrades to <c>null</c>/empty and is traced via <see cref="Diagnostics"/>,
/// never thrown — except cancellation, which propagates.
/// </remarks>
internal sealed class RpcEditorSurface : IEditorSurface
{
    private readonly JsonRpc             _rpc;
    private readonly OpenDocumentOverlay _overlay;
    private readonly bool                _hasEditor;
    private volatile string?             _activePath;

    /// <param name="hasEditor">The adapter has an editor to answer about (<see cref="InitializeParams.EditorSurface"/>).
    /// Without one nothing is asked of it: the editor tools are not offered, and every read answers "none".</param>
    public RpcEditorSurface(JsonRpc rpc, OpenDocumentOverlay overlay, bool hasEditor = true)
    {
        _rpc       = rpc;
        _overlay   = overlay;
        _hasEditor = hasEditor;
    }

    /// <summary>Updated from the adapter's `editor/didChangeActiveDocument` notification.</summary>
    public void SetActiveDocument(string? path) => _activePath = path;

    // A connected adapter is a precondition of the host process existing at all — when it has an editor.
    public bool IsAvailable => _hasEditor;

    public bool HasEditor => _hasEditor;

    public string? ActiveDocumentPath => _hasEditor ? _activePath : null;

    // The adapter mirrors every open document into the overlay (didOpen/didClose),
    // so the overlay's key set IS the open-editors list — no RPC round-trip needed.
    // Without an editor the overlay holds only the files found unsaved, which are no list of open editors.
    public IReadOnlyList<string> GetOpenDocumentPaths() => _hasEditor ? _overlay.Paths : [];

    public async Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct)
    {
        if (!_hasEditor) return null;
        // Overlay first: the buffer is already mirrored and is fresher than a round-trip.
        var cached = _activePath;
        if (cached is not null && _overlay.TryGet(cached, out var text))
            return new ActiveDocument(cached, text);

        try
        {
            var doc = await _rpc.InvokeWithCancellationAsync<ActiveDocumentDto?>(
                "editor/activeDocument", cancellationToken: ct);
            return string.IsNullOrEmpty(doc?.Path) ? null : new ActiveDocument(doc!.Path!, doc.Text ?? string.Empty);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("RpcEditorSurface.GetActiveDocument", ex);
            return null;
        }
    }

    public async Task<string?> InsertAtCursorAsync(string text, CancellationToken ct)
    {
        if (!_hasEditor) return null;
        try
        {
            return await _rpc.InvokeWithParameterObjectAsync<string?>(
                "editor/insertAtCursor", new { text }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("RpcEditorSurface.InsertAtCursor", ex);
            return null;
        }
    }

    public async Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct)
    {
        if (!_hasEditor) return null;   // no editor to ask: the tool builds instead
        try
        {
            return await _rpc.InvokeWithCancellationAsync<string?>(
                "editor/diagnostics", cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("RpcEditorSurface.GetEditorDiagnostics", ex);
            return null;   // adapter without the handler → the tool builds instead
        }
    }

    public async Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct)
    {
        if (!_hasEditor) return null;
        try
        {
            var result = await _rpc.InvokeWithParameterObjectAsync<EditResultDto?>(
                "editor/replaceSelection", new { text }, ct);
            return string.IsNullOrEmpty(result?.Path)
                ? null
                : new EditorEditResult(result!.Path!, result.ReplacedSelection);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("RpcEditorSurface.ReplaceSelection", ex);
            return null;
        }
    }
}
