namespace Inferpal.Services.Signals;

/// <summary>What an editor adapter does with the workspace root on this pass.</summary>
internal enum RootPinAction { None, Pin, Index }

/// <summary>
/// Decides when an editor adapter pins <c>ProjectIndexService.RootDir</c> — the root the file tools
/// confine writes to, the approval service reads <c>.inferpal/permissions.json</c> from, and Smart
/// Fix reads <c>validators.json</c> from.
/// </summary>
/// <remarks>
/// That root is not a RAG setting. An empty one switches all three off at once:
/// <c>PathSanitizer.AssertUnderRoot</c> accepts any path and the deny overlay is never read, while
/// <c>/permissions</c> still lists it. So it is pinned whether or not indexing runs; RAG only decides
/// whether a solution switch also starts an indexing pass.
/// A pinned root follows the authoritative solution signal only, never open files: a file opened
/// from another folder must not re-point the confinement.
/// </remarks>
internal static class WorkspaceRootPin
{
    /// <param name="ragEnabled">Whether a solution switch also re-indexes.</param>
    /// <param name="currentRoot">The root pinned so far (empty = none).</param>
    /// <param name="activeSolutionDir">The solution the in-process package reports open, if any.</param>
    /// <param name="reliableRoot">A solution-anchored root (<c>ProjectRootLocator.LocateReliable</c>),
    /// consulted only while nothing is pinned.</param>
    internal static (RootPinAction Action, string? Root) Decide(
        bool ragEnabled, string? currentRoot, string? activeSolutionDir, string? reliableRoot)
    {
        if (string.IsNullOrEmpty(currentRoot))
            return string.IsNullOrEmpty(reliableRoot)
                ? (RootPinAction.None, null)
                // Pinned even with RAG on: the startup pass indexes it seconds later, and the tools
                // must not run unconfined in between.
                : (RootPinAction.Pin, reliableRoot);

        if (string.IsNullOrEmpty(activeSolutionDir)
            || string.Equals(activeSolutionDir, currentRoot, StringComparison.OrdinalIgnoreCase))
            return (RootPinAction.None, null);

        return (ragEnabled ? RootPinAction.Index : RootPinAction.Pin, activeSolutionDir);
    }
}
