using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.RpcContracts.Editor;

namespace Inferpal.Services.VsIntegration;

/// <summary>Did an <c>EditAsync</c> call change the document?</summary>
/// <remarks>
/// ⚠ An edit the editor refuses is RETURNED, not thrown: the document changed under it, was closed, or is too many
/// versions ahead — reported per document in the response. Every caller reads it: unread, a refused edit is reported
/// as applied (or not reported at all) after the user has waited for the model.
/// </remarks>
internal static class VsEditResult
{
    public static bool Applied(EditResponse response) =>
        Applied(response.Succeeded, response.DocumentEditResults?.Values.Select(r => r.EditResult) ?? []);

    /// <summary>Applied = the call succeeded and every document took its changes.</summary>
    internal static bool Applied(bool succeeded, IEnumerable<EditResult> documents) =>
        succeeded && documents.All(r => r == EditResult.Success);
}
