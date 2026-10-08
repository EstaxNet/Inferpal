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
    /// <returns>
    /// <c>true</c> only when the text is on the clipboard: an error, or a copy still waiting when the budget ran out, is
    /// <c>false</c>. ⚠ A caller that ANNOUNCES the copy reads it: "copied — paste it into an issue" over a failed copy
    /// has the user paste whatever the clipboard held before, a secret as likely as not.
    /// </returns>
    public static bool TrySet(string? text, string context)
    {
        var landed = Copy(text, context);
        if (!landed)
        {
            try { NotCopied?.Invoke(); }
            catch (Exception ex) { Diagnostics.Swallow(context, ex); }
        }
        return landed;
    }

    /// <summary>
    /// Raised when a copy did not land, whoever asked: the window says it in the conversation.
    /// </summary>
    /// <remarks>⚠ A copy button announces nothing and gives no other sign: the user went on to paste what the clipboard
    /// held before.</remarks>
    internal static event Action? NotCopied;

    private static bool Copy(string? text, string context)
    {
        // Clipboard.SetText(string.Empty) throws: keep the historical single-space placeholder.
        var payload = string.IsNullOrEmpty(text) ? " " : text;
        var copied  = false;
        try
        {
            var thread = new Thread(() =>
            {
                try { System.Windows.Clipboard.SetText(payload); copied = true; }
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
            {
                Diagnostics.Record(context,
                    $"The clipboard was still held after {JoinBudget.TotalSeconds:0}s; the copy was "
                    + "left to finish on its own. Another process holds the clipboard.");
                return false;   // not known to have landed
            }
            return copied;
        }
        catch (Exception ex) { Diagnostics.Swallow(context, ex); return false; }
    }
}
