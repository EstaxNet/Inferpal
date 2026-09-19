using System.Threading;
using Inferpal.Services;

namespace Inferpal.ToolWindow;

/// <summary>
/// The one way to open a WPF dialog from the VM context: a dedicated STA thread whose task
/// <b>always completes</b>.
/// </summary>
/// <remarks>
/// <para>
/// Raised by hand at each site, the thread comes out different every time, and two mistakes are
/// expensive:
/// </para>
/// <list type="bullet">
/// <item>without a <c>try</c>, an exception out of <c>ShowDialog</c> leaves the
/// <c>TaskCompletionSource</c> pending forever — the command waits with no message — and, on a
/// <b>foreground</b> thread, an unhandled exception <b>terminates the process</b>;</item>
/// <item>without <c>IsBackground</c>, a stuck thread keeps the process from exiting.</item>
/// </list>
/// <para>
/// ⚠ The exception is <b>propagated</b>, not swallowed: the callers render their own error message.
/// The <c>TrySetResult</c> in the <c>finally</c> is only a net — if some path leaves without posting
/// anything, the wait unwinds as "cancelled" rather than hanging.
/// </para>
/// </remarks>
internal static class StaDialog
{
    /// <param name="show">What the STA thread runs; its return value is the task's.</param>
    /// <param name="name">Thread name, visible in the debugger and in traces.</param>
    public static Task<T?> RunAsync<T>(Func<T?> show, string name)
    {
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(show());
            }
            catch (Exception ex)
            {
                Diagnostics.Swallow(name, ex);   // traced here, handed to the caller right after
                tcs.TrySetException(ex);
            }
            finally
            {
                tcs.TrySetResult(default);       // net: the task is never left pending
            }
        })
        {
            IsBackground = true,
            Name         = "Inferpal-" + name,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
