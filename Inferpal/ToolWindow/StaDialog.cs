using System.Threading;
using Inferpal.Services;

namespace Inferpal.ToolWindow;

/// <summary>
/// The one way to open a WPF dialog from the VM context: a dedicated STA thread whose task
/// <b>always completes</b>.
/// </summary>
/// <remarks>
/// <para>
/// Measured: four sites raised that thread by hand, and they did not raise it the same way:
/// </para>
/// <list type="bullet">
/// <item>the attachment picker and the export picker had <b>no</b> <c>try</c> at all: an exception
/// out of <c>ShowDialog</c> left the <c>TaskCompletionSource</c> pending forever — the command
/// waited with no message — and, on a <b>foreground</b> thread, an unhandled exception
/// <b>terminates the process</b>;</item>
/// <item>the pinned-file picker did it all correctly (try / catch / finally);</item>
/// <item>none of the three set <c>IsBackground</c>, while the two inline-edit windows have done so
/// since they were written — a stuck foreground thread keeps the process from exiting.</item>
/// </list>
/// <para>
/// The correct shape was therefore already in the repository, twice
/// (<c>InlineEditInputWindow.CreateAndShowAsync</c>): it simply had not been applied everywhere.
/// </para>
/// <para>
/// ⚠ The exception is <b>propagated</b> (not swallowed): the callers already have their error
/// message, and a silent failure is exactly what this review was hunting. The
/// <c>TrySetResult</c> in the <c>finally</c> is only a net — if some path left without posting
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
