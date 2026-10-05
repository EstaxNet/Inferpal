using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Inferpal.Services;

namespace Inferpal.GhostText;

/// <summary>
/// In-process <see cref="IVsUpdateSolutionEvents"/> subscriber.
/// Instantiated by <see cref="GhostTextPackage.InitializeAsync"/> on the VS UI thread.
/// When VS finishes a build with errors, collects the error messages directly from
/// <c>IVsTaskList</c> (the VS Error List) and writes them to <see cref="BuildSignalFile"/>
/// so that the out-of-process <see cref="VsBuildMonitor"/> can react via
/// <c>FileSystemWatcher</c> — without having to run a second <c>dotnet build</c>.
/// </summary>
internal sealed class VsBuildEventHandler : IVsUpdateSolutionEvents, IDisposable
{
    private readonly IVsSolutionBuildManager2 _buildManager;
    private readonly IVsSolution              _solution;
    private readonly IVsTaskList?             _taskList;
    private          uint                     _cookie;

    /// <summary>
    /// Connects to the VS build manager.  Must be called on the UI thread.
    /// </summary>
    /// <param name="taskList">
    /// Optional reference to the VS Task List service (<c>SVsTaskList</c>).
    /// When provided, build errors are collected from the VS Error List and embedded
    /// in the signal file, allowing the OOP monitor to skip the second
    /// <c>dotnet build</c> pass entirely.
    /// </param>
    internal VsBuildEventHandler(
        IVsSolutionBuildManager2 buildManager,
        IVsSolution              solution,
        IVsTaskList?             taskList = null)
    {
        _buildManager = buildManager;
        _solution     = solution;
        _taskList     = taskList;
#pragma warning disable VSTHRD010  // caller (GhostTextPackage) already switched to main thread
        buildManager.AdviseUpdateSolutionEvents(this, out _cookie);
#pragma warning restore VSTHRD010
    }

    // ── IVsUpdateSolutionEvents ────────────────────────────────────────────────

    int IVsUpdateSolutionEvents.UpdateSolution_Begin(ref int pfCancelUpdate) => VSConstants.S_OK;

    int IVsUpdateSolutionEvents.UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
    {
        // fSucceeded == 0  →  at least one project failed.
        // fCancelCommand != 0  →  user cancelled; don't offer AI fix.
        if (fCancelCommand != 0) return VSConstants.S_OK;

        // UpdateSolution_Done is called on the VS UI thread — safe to call IVsSolution here.
        var solutionPath = GetSolutionPath();
        if (string.IsNullOrEmpty(solutionPath)) return VSConstants.S_OK;

        // ⚠ A success is said too: the "last build failed" banner of an earlier build otherwise stayed up after the
        // user fixed the errors and built again — the one moment it is plainly false.
        if (fSucceeded != 0)
        {
            _ = Task.Run(() => BuildSignalFile.Write(solutionPath!, errorLines: null, errorCount: 0, succeeded: true));
            return VSConstants.S_OK;
        }

        // VS populates the Error List asynchronously after UpdateSolution_Done fires.
        // Collecting errors synchronously here often returns an empty list.
        // We fire-and-forget an async task that waits briefly before collecting,
        // then writes the signal file for the OOP VsBuildMonitor to consume.
        _ = CollectAndSignalAsync(solutionPath!);
        return VSConstants.S_OK;
    }

    /// <summary>
    /// Waits for VS to finish populating its Error List, collects errors on the UI thread,
    /// then writes the IPC signal file.  Fire-and-forget from <see cref="UpdateSolution_Done"/>.
    /// </summary>
    private async Task CollectAndSignalAsync(string solutionPath)
    {
        try
        {
            // Allow VS time to populate the Error List before we enumerate it.
            // 800 ms covers both fast and slow builds on typical machines.
            await Task.Delay(800).ConfigureAwait(false);

            // Error collection via IVsTaskList requires the VS UI thread.
            List<string> errors = new();
            var errorCount = 0;
            try
            {
#pragma warning disable VSTHRD010  // SwitchToMainThreadAsync handles the transition
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                (errors, errorCount) = CollectBuildErrors();
#pragma warning restore VSTHRD010
            }
            catch { /* non-critical — signal will be written with empty errors */ }

            // Write the signal from a background thread (file I/O should not block UI).
            await Task.Run(() => BuildSignalFile.Write(solutionPath, errors, errorCount))
                      .ConfigureAwait(false);
        }
        catch { /* non-critical */ }
    }

    int IVsUpdateSolutionEvents.UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;
    int IVsUpdateSolutionEvents.UpdateSolution_Cancel()                            => VSConstants.S_OK;
    int IVsUpdateSolutionEvents.OnActiveProjectCfgChange(IVsHierarchy pIHierProj) => VSConstants.S_OK;

    // ── Error collection ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads error-level task items from the VS Error List via <c>IVsTaskList</c>.
    /// Must be called on the UI thread (same constraint as <c>UpdateSolution_Done</c>).
    /// Returns an empty list if <c>_taskList</c> is unavailable or an exception occurs.
    /// ⚠ The lines stop at <see cref="MaxErrorLines"/>, the COUNT does not: announced from the lines, a build with 200
    /// errors read "30 error(s)".
    /// </summary>
    private (List<string> Lines, int Count) CollectBuildErrors()
    {
        var result = new List<string>();
        var count  = 0;
        if (_taskList is null) return (result, count);
        try
        {
#pragma warning disable VSTHRD010  // called from UpdateSolution_Done which runs on the UI thread
            if (_taskList.EnumTaskItems(out var enumItems) != VSConstants.S_OK || enumItems is null)
                return (result, count);

            var itemBuf    = new IVsTaskItem[1];
            var fetchedBuf = new uint[1];

            // COM [in,out] VSTASKPRIORITY* maps to VSTASKPRIORITY[] in the C# interop.
            var priArr = new VSTASKPRIORITY[1];

            while (enumItems.Next(1, itemBuf, fetchedBuf) == VSConstants.S_OK
                   && fetchedBuf[0] == 1)
            {
                var item = itemBuf[0];
                if (item is null) continue;

                // Filter: errors only (TP_HIGH).  TP_NORMAL = warnings, TP_LOW = messages.
                priArr[0] = VSTASKPRIORITY.TP_LOW;
                item.get_Priority(priArr);
                if (priArr[0] != VSTASKPRIORITY.TP_HIGH) continue;

                item.get_Text(out var text);
                if (string.IsNullOrWhiteSpace(text)) continue;

                count++;
                if (result.Count >= MaxErrorLines) continue;

                // Build a location prefix if the task carries file/line information.
                // COM [out] BSTR → C# method named "Document"/"Line" (no get_ prefix).
                string? doc  = null;
                int     line = 0;
                try { item.Document(out doc); }   catch { /* optional */ }
                try { item.Line(out line); }       catch { /* optional */ }

                var location = string.IsNullOrEmpty(doc)
                    ? string.Empty
                    : $"{Path.GetFileName(doc)}({line + 1}): ";

                result.Add($"{location}error: {text.Trim()}");
            }
#pragma warning restore VSTHRD010
        }
        catch { /* non-critical */ }
        return (result, count);
    }

    /// <summary>The error lines carried to the out-of-process side — the count is not capped.</summary>
    private const int MaxErrorLines = 30;

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full path of the open .sln file, or <c>null</c> if no solution is loaded.
    /// Must be called on the UI thread.
    /// </summary>
    private string? GetSolutionPath()
    {
        try
        {
#pragma warning disable VSTHRD010  // called from UpdateSolution_Done which runs on the UI thread
            _solution.GetSolutionInfo(out var dir, out var file, out _);
#pragma warning restore VSTHRD010
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(file))
                return Path.Combine(dir, file);
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("Build.GetSolutionPath", ex); }
        return null;
    }

    // ── IDisposable ────────────────────────────────────────────────────────────

    /// <summary>Unregisters the build events subscription.  Must be called on the UI thread.</summary>
    public void Dispose()
    {
        if (_cookie == 0) return;
        try
        {
#pragma warning disable VSTHRD010  // caller (GhostTextPackage.Dispose) switches to main thread
            _buildManager.UnadviseUpdateSolutionEvents(_cookie);
#pragma warning restore VSTHRD010
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("Build.Unsubscribe", ex); }
        _cookie = 0;
    }
}
