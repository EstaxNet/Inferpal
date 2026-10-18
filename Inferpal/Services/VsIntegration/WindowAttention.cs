using System.Runtime.InteropServices;

namespace Inferpal.Services.VsIntegration;

/// <summary>
/// Draws attention to the Visual Studio window that owns this out-of-process host: its taskbar button flashes until
/// the window comes to the foreground — for an approval card that waits while its author is elsewhere.
/// </summary>
/// <remarks>
/// The window is the main window of the devenv this host belongs to, as resolved once at startup for the signal
/// channels (<see cref="Signals.SignalScope.VsInstanceKey"/>): one source for "which Visual Studio is mine".
/// <c>FlashWindowEx</c> takes a window of another process: nothing in the extensibility SDK says "this needs you".
/// Best-effort: a window that cannot be found is said once in /diagnostics.
/// </remarks>
internal static class WindowAttention
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint   Size;
        public IntPtr Window;
        public uint   Flags;
        public uint   Count;
        public uint   Timeout;
    }

    private const uint FlashTray      = 0x2;   // FLASHW_TRAY
    private const uint FlashUntilSeen = 0xC;   // FLASHW_TIMERNOFG: until the window comes to the foreground

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    private const uint FlashStop      = 0x0;   // FLASHW_STOP

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <summary>What <see cref="Request"/> did.</summary>
    internal enum Outcome
    {
        /// <summary>A window of this Visual Studio is in front — the main one, a floating tool window, a dialog: its user
        /// is looking, and the caller asks again later.</summary>
        Looking,
        /// <summary>The taskbar button flashes: <see cref="Stop"/> once the card is answered.</summary>
        Flashed,
        /// <summary>No window to flash (none resolved, gone).</summary>
        Unavailable,
    }

    /// <summary>
    /// Flashes the owning Visual Studio's taskbar button — unless one of its windows is in front.
    /// </summary>
    /// <remarks>⚠ "In front" is any window of the devenv process, not only its main window: a card read in a floating
    /// tool window on a second screen would flash the taskbar under the reader's eyes, and the flash — which stops by
    /// itself only when the MAIN window comes to the foreground — would outlive the answer.</remarks>
    internal static Outcome Request()
    {
        try
        {
            if (MainWindow() is not { } window) return Outcome.Unavailable;
            GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
            if (Signals.SignalScope.VsInstanceKey is int devenv && foreground == (uint)devenv) return Outcome.Looking;
            Flash(window, FlashTray | FlashUntilSeen);
            return Outcome.Flashed;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Diagnostics.Swallow("WindowAttention.Request", ex);
            return Outcome.Unavailable;
        }
    }

    /// <summary>Stops a flash <see cref="Request"/> started: the card it was for is answered.</summary>
    internal static void Stop()
    {
        try
        {
            if (MainWindow() is { } window) Flash(window, FlashStop);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Diagnostics.Swallow("WindowAttention.Stop", ex);
        }
    }

    private static IntPtr? MainWindow()
    {
        if (Signals.SignalScope.VsInstanceKey is not int devenv)
        {
            Diagnostics.RecordOnce("WindowAttention",
                "No owning devenv was resolved at startup: a waiting approval card cannot flash the taskbar.", "nodevenv");
            return null;
        }
        using var process = System.Diagnostics.Process.GetProcessById(devenv);
        return process.MainWindowHandle == IntPtr.Zero ? null : process.MainWindowHandle;
    }

    private static void Flash(IntPtr window, uint flags)
    {
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = flags };
        FlashWindowEx(ref info);
    }
}
