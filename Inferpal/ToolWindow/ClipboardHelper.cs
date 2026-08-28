using System.Threading;
using Inferpal.Services;

namespace Inferpal.ToolWindow;

/// <summary>
/// The one way UI code copies text to the Windows clipboard (§27.6 — five sites duplicated the
/// try/Swallow guard with drifting details: the STA dance present or not, the empty-string guard
/// present or not; the X-Ray site called WPF from the view-model context, not STA).
/// </summary>
internal static class ClipboardHelper
{
    /// <summary>
    /// Copies <paramref name="text"/> best-effort from any thread: a dedicated STA thread is spun
    /// up (WPF clipboard requires STA), contention (CLIPBRD_E_CANT_OPEN when another process holds
    /// the clipboard) is swallowed — a copy button must never crash the tool window.
    /// </summary>
    /// <param name="context">Diagnostics context, e.g. <c>"Clipboard.CopyMessage"</c>.</param>
    public static void TrySet(string? text, string context)
    {
        // Clipboard.SetText(string.Empty) throws: keep the historical single-space placeholder.
        var payload = string.IsNullOrEmpty(text) ? " " : text;
        try
        {
            var thread = new Thread(() =>
            {
                try { System.Windows.Clipboard.SetText(payload); }
                catch (Exception ex) { Diagnostics.Swallow(context, ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        catch (Exception ex) { Diagnostics.Swallow(context, ex); }
    }
}
