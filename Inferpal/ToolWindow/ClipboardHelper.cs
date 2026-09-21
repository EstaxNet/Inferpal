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
    /// <summary>How long the caller waits for the STA thread. Well above a contended clipboard
    /// (WPF retries internally for about a second), well below "the tool window is frozen".</summary>
    private static readonly TimeSpan JoinBudget = TimeSpan.FromSeconds(3);

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
            })
            {
                // ⚠ BACKGROUND, and the exemption this file holds is what hid it: it is exempt from
                // the shared STA funnel because it owns its thread for another reason — which
                // exempts it from the FUNNEL, not from the property the funnel carries. A
                // foreground thread blocked on a clipboard another process is holding keeps
                // Visual Studio itself from exiting: the user closes the IDE and the process stays.
                IsBackground = true,
                Name         = "inferpal-clipboard",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            // ⚠ And the wait is BOUNDED. `Clipboard.SetText` blocks while another process holds the
            // clipboard (the CLIPBRD_E_CANT_OPEN this method already swallows), and an unbounded
            // Join froze whoever asked — these five call sites are copy buttons on the tool window.
            // The copy may still land after we stop waiting; that is the best-effort contract this
            // method already has. What must not happen is the window freezing with it.
            if (!thread.Join(JoinBudget))
                Diagnostics.Record(context,
                    $"The clipboard was still held after {JoinBudget.TotalSeconds:0}s; the copy was "
                    + "left to finish on its own. Another process holds the clipboard.");
        }
        catch (Exception ex) { Diagnostics.Swallow(context, ex); }
    }
}
