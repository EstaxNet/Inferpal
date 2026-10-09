using Inferpal.Services.Editor;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Services.VsIntegration;

/// <summary>
/// Asks Visual Studio which open documents hold unsaved changes, and their text — the source of the
/// <see cref="OpenDocumentOverlay"/> the tools consult before reading or writing a file.
/// </summary>
/// <remarks>
/// ⚠ Without it, the writing tools never see an unsaved buffer: the agent writes the file on disk, Visual Studio asks
/// "reload?", and yes erases what the user had typed. Asked on demand (before every tool call, again after an
/// approval), never mirrored from events: a mirror that missed a save would refuse every write to that file.
/// </remarks>
internal static class VsUnsavedDocuments
{
    public static async Task<IReadOnlyList<UnsavedDocument>> ReadAsync(VisualStudioExtensibility vs, CancellationToken ct)
    {
        var documents = vs.Documents();
        var open      = await documents.GetOpenDocumentsAsync(ct);
        var unsaved   = new List<UnsavedDocument>();
        foreach (var document in open)
        {
            if (!document.IsDirty || document.Moniker is not { IsFile: true } moniker) continue;

            // A dirty document that is not a text buffer (a designer) is still unsaved: refused, read from disk.
            string? text = null;
            try { text = (await documents.GetTextDocumentSnapshotAsync(document, ct))?.Text.CopyToString(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Diagnostics.Swallow("VsUnsavedDocuments.Text", ex); }

            unsaved.Add(new UnsavedDocument(moniker.LocalPath, text));
        }
        return unsaved;
    }
}
