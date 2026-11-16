using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.RpcContracts.ToolWindows;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.ServiceBroker;
using Microsoft.VisualStudio.Shell.Settings;
using Task = System.Threading.Tasks.Task;

namespace Inferpal.GhostText;

/// <summary>
/// Reopens the chat at startup when it was open when Visual Studio last closed.
/// </summary>
/// <remarks>
/// ⚠ The chat's window does not let Visual Studio create it from the saved layout (<c>AllowAutoCreation = false</c> on
/// <c>InferpalToolWindow</c>): restored that way, Visual Studio builds the panel before the extension's host has
/// registered it, and the panel shows "tool window not found" instead of the chat until the next start. Shown here
/// through the platform's tool window manager — the path the menu takes — it waits for the host instead.
/// </remarks>
internal static class ChatWindowRestore
{
    /// <summary>The chat window's identifier: the full name of its type in the out-of-process extension.</summary>
    internal const string Identifier = "Inferpal.InferpalToolWindow";

    /// <summary>The frame Visual Studio keeps for that window (<c>RST:0:0:{guid}</c> in its layout), derived from the
    /// identifier.</summary>
    /// <remarks>⚠ It changes with <see cref="Identifier"/>: a renamed window type makes this frame unknown, the chat is
    /// then recorded closed at every close and never reopened. Read the new value from the layout
    /// (<c>CurrentSettings.vssettings</c>, <c>RST:0:0:</c>) after such a rename.</remarks>
    internal static readonly Guid FrameGuid = new("b012f8a2-cb76-6038-7dcd-089523f4ba47");

    private const string Collection = "Inferpal";
    private const string OpenAtClose = "ChatOpenAtClose";

    /// <summary>How long a reopening keeps trying while the extension's host is not there yet.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    /// <summary>Records whether the chat is open, as Visual Studio starts closing (its windows are still there). UI
    /// thread.</summary>
    internal static void RecordAtClose(IServiceProvider services)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var open = IsChatOpen(services);
            var store = new ShellSettingsManager(services).GetWritableSettingsStore(SettingsScope.UserSettings);
            if (!store.CollectionExists(Collection)) store.CreateCollection(Collection);
            store.SetBoolean(Collection, OpenAtClose, open);
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("ChatRestore.Record", ex); }
    }

    private static bool IsChatOpen(IServiceProvider services)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (services.GetService(typeof(SVsUIShell)) is not IVsUIShell shell
            || shell.GetToolWindowEnum(out var frames) != VSConstants.S_OK || frames is null)
            return false;
        var one = new IVsWindowFrame[1];
        while (frames.Next(1, one, out var fetched) == VSConstants.S_OK && fetched == 1)
        {
            var frame = one[0];
            if (frame.GetGuidProperty((int)__VSFPROPID.VSFPROPID_GuidPersistenceSlot, out var slot) == VSConstants.S_OK
                && slot == FrameGuid)
                return frame.IsVisible() == VSConstants.S_OK;
        }
        return false;
    }

    /// <summary>Shows the chat, without taking the focus, when it was open at the last close; retried while the host
    /// is not there yet.</summary>
    internal static async Task ReopenAsync(AsyncPackage package, CancellationToken ct)
    {
        try
        {
            var store = new ShellSettingsManager(package).GetReadOnlySettingsStore(SettingsScope.UserSettings);
            // Never recorded (the first start of a version that restores it this way): reopened, as Visual Studio's own
            // layout did before.
            if (!store.GetBoolean(Collection, OpenAtClose, true)) return;
            if (await package.GetServiceAsync(typeof(SVsBrokeredServiceContainer)) is not IBrokeredServiceContainer container)
                return;
            var broker = container.GetFullAccessServiceBroker();
            var until = DateTime.UtcNow + Budget;
            Exception? last = null;
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                IToolWindowManager? manager = null;
                try
                {
                    manager = await broker.GetProxyAsync<IToolWindowManager>(
                        Microsoft.VisualStudio.VisualStudioServices.VS2022_3.ToolWindowManager, ct);
                    if (manager is not null) { await manager.ShowAsync(Identifier, false, ct); return; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; }
                finally { (manager as IDisposable)?.Dispose(); }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            if (last is not null) Services.Diagnostics.Swallow("ChatRestore.Show", last);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Services.Diagnostics.Swallow("ChatRestore.Reopen", ex); }
    }
}
