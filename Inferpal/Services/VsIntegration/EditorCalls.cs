namespace Inferpal.Services.VsIntegration;

/// <summary>
/// A call to Visual Studio that a window's gesture starts and never waits for: opening a document, showing a tool window.
/// </summary>
/// <remarks>
/// ⚠ Visual Studio's answer to such a call can wait on the window's own context, which the gesture holds until it returns:
/// then it never comes, and every later gesture of that window — Send, New conversation — queues behind it, the window
/// frozen without a word. The call is started here and left running, bounded by <see cref="Budget"/> (its token cancels the
/// request in Visual Studio too); an overrun and a failure are traced, and a failure is said when the gesture was the
/// person's (<c>onFailure</c>), who would otherwise see nothing happen.
/// </remarks>
internal static class EditorCalls
{
    /// <summary>How long Visual Studio has to answer before the wait is given up and said.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>Starts <paramref name="call"/> and returns at once.</summary>
    /// <param name="call">The call, given the budget's token.</param>
    /// <param name="context">The <see cref="Diagnostics"/> context its trace goes under.</param>
    /// <param name="what">What the call does, for the trace ("opening notes.md").</param>
    /// <param name="onFailure">What to do when it fails, other than running out of time: say it, undo a state.</param>
    /// <param name="then">What follows once Visual Studio has answered (a panel opened in the window just shown).</param>
    internal static void Start(Func<CancellationToken, Task> call, string context, string what, Func<Exception, Task>? onFailure = null,
                               Func<Task>? then = null) =>
        _ = RunAsync(call, context, what, onFailure, then);

    private static async Task RunAsync(Func<CancellationToken, Task> call, string context, string what, Func<Exception, Task>? onFailure,
                                       Func<Task>? then)
    {
        using var budget = new CancellationTokenSource(Budget);
        try
        {
            await call(budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
            if (then is not null) await then().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            Diagnostics.Record(context, $"Visual Studio did not confirm {what} within {Budget.TotalSeconds:0} s; the gesture went on.");
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow(context, ex);
            if (onFailure is null) return;
            try { await onFailure(ex).ConfigureAwait(false); }
            catch (Exception said) { Diagnostics.Swallow(context, said); }
        }
    }
}
