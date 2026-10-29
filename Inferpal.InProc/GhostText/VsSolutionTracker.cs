using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Inferpal.Services;

namespace Inferpal.GhostText;

/// <summary>
/// In-process <see cref="IVsSolutionEvents"/> subscriber.
/// Instantiated by <see cref="GhostTextPackage.InitializeAsync"/> on the VS UI thread.
/// Publishes the currently open solution to <see cref="ActiveSolutionSignal"/> so the
/// out-of-process extension host can resolve the real solution root (for <c>/solution</c>,
/// <c>/map</c>, RAG indexing, …) instead of relying on the host process working directory,
/// which never follows solution open/close.
/// </summary>
internal sealed class VsSolutionTracker : IVsSolutionEvents, IVsSolutionEvents7, IDisposable
{
    private readonly IVsSolution _solution;
    private          uint        _cookie;

    /// <summary>Subscribes to solution events and publishes the current state. UI-thread only.</summary>
    internal VsSolutionTracker(IVsSolution solution)
    {
        _solution = solution;
#pragma warning disable VSTHRD010  // caller (GhostTextPackage) already switched to the main thread
        solution.AdviseSolutionEvents(this, out _cookie);
#pragma warning restore VSTHRD010
        // A solution may already be open by the time the package loads — publish it now.
        PublishCurrent();
    }

    /// <summary>What was last published, the solution or the folder: one write per solution, not per project.</summary>
    private string? _published;

    /// <summary>
    /// Reads the open solution from VS and writes the signal. UI-thread only.
    /// <para>
    /// Deliberately does <em>not</em> clear the signal when <c>GetSolutionInfo</c> reports no file:
    /// when a brand-new solution is created, <c>OnAfterOpenSolution</c> can fire <em>before</em> the
    /// <c>.sln</c> has been persisted to disk, so the file name is transiently empty. Clearing here
    /// would wipe the signal and never restore it, leaving readers to fall back to a stale, unrelated
    /// solution. Clearing is therefore owned solely by the explicit close events below; the new
    /// solution is re-published by <see cref="IVsSolutionEvents.OnAfterOpenProject"/> once its first
    /// project (and thus the saved <c>.sln</c>) is loaded.
    /// </para>
    /// </summary>
    private void PublishCurrent()
    {
        try
        {
            // ⚠ Published once per solution: re-published for each project it loads, a 300-project solution made some
            // 600 staged writes on devenv's UI thread for the same path.
#pragma warning disable VSTHRD010  // called from UI-thread contexts (ctor / solution events)
            _solution.GetSolutionInfo(out var dir, out var file, out _);
            // ⚠ A folder opened without a solution is the workspace too: published as a folder, whatever file name VS
            // reports for it, so that it is never read as a solution — and the tools are not left without a root.
            if (_solution.GetProperty((int)__VSPROPID7.VSPROPID_IsInOpenFolderMode, out var folderMode) == VSConstants.S_OK
                && folderMode is true)
            {
                // Remembered once written only: a write that failed is tried again at the next project.
                if (!string.IsNullOrEmpty(dir) && dir != _published && Directory.Exists(dir) && ActiveSolutionSignal.WriteFolder(dir))
                    _published = dir;
                return;
            }
#pragma warning restore VSTHRD010
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(file))
            {
                var full = Path.Combine(dir, file);
                // Only publish once the .sln actually exists on disk — avoids recording a path the
                // OOP reader would reject (it validates File.Exists) for a not-yet-saved solution.
                if (full != _published && File.Exists(full))
                {
                    if (ActiveSolutionSignal.Write(full)) _published = full;
                    // Also seed the durable last-known cache so detection survives this signal being
                    // cleared on close (or the package not loading in a later session).
                    LastKnownSolutionFile.Record(full);
                }
            }
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("Solution.Publish", ex); }
    }

    // ── IVsSolutionEvents ──────────────────────────────────────────────────────

    int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        PublishCurrent();
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved)
    {
        ActiveSolutionSignal.Clear();
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved)
    {
        _published = null;
        ActiveSolutionSignal.Clear();
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
    {
        // Re-publish: for a newly created solution OnAfterOpenSolution may have fired before the
        // .sln was saved (empty file name → nothing written). By the time the first project loads
        // the .sln exists on disk, so this catches the case PublishCurrent could not on open.
        PublishCurrent();
        return VSConstants.S_OK;
    }
    int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved)    => VSConstants.S_OK;
    int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel)          => VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel)    => VSConstants.S_OK;

    // ── IVsSolutionEvents7 (Open Folder) ───────────────────────────────────────

    void IVsSolutionEvents7.OnAfterOpenFolder(string folderPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath) && ActiveSolutionSignal.WriteFolder(folderPath))
                _published = folderPath;
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("Solution.OpenFolder", ex); }
    }

    void IVsSolutionEvents7.OnBeforeCloseFolder(string folderPath) { _published = null; ActiveSolutionSignal.Clear(); }
    void IVsSolutionEvents7.OnAfterCloseFolder(string folderPath)  { _published = null; ActiveSolutionSignal.Clear(); }
    void IVsSolutionEvents7.OnQueryCloseFolder(string folderPath, ref int pfCancel) { }
    void IVsSolutionEvents7.OnAfterLoadAllDeferredProjects() { }

    // ── IDisposable ────────────────────────────────────────────────────────────

    /// <summary>Unregisters the subscription. Must be called on the UI thread.</summary>
    public void Dispose()
    {
        if (_cookie == 0) return;
        try
        {
#pragma warning disable VSTHRD010  // caller (GhostTextPackage.Dispose) switches to the main thread
            _solution.UnadviseSolutionEvents(_cookie);
#pragma warning restore VSTHRD010
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("Solution.Unsubscribe", ex); }
        _cookie = 0;
        ActiveSolutionSignal.Clear();
    }
}
