namespace Inferpal.Services.Tools;

/// <summary>
/// What a tool says while it runs — each action <c>interact_page</c> or <c>interact_window</c> plays, as it is played — on the
/// status line of the run that called it (the run's <c>onStep</c>, which both editors show while a turn works).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Carried by the call's asynchronous flow (<see cref="AsyncLocal{T}"/>): a batch of calls run in parallel each reports to
/// the run that started it, and a tool called outside a run says nothing.
/// </para>
/// <para>
/// ⚠ The flow keeps a SCOPE, closed when the run ends, never the run's callback itself: work a tool starts during the run (a
/// background job, a browser's pump) carries the flow on after it, and would otherwise keep the finished turn's status line
/// alive — and write on it.
/// </para>
/// </remarks>
internal static class ToolProgress
{
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>Routes what the tools of this flow say to <paramref name="onStep"/> until disposed; once disposed, nothing
    /// said in this flow, or in work started from it, reaches <paramref name="onStep"/>.</summary>
    internal static IDisposable Route(Action<string> onStep)
    {
        var scope = new Scope(onStep, Current.Value);
        Current.Value = scope;
        return scope;
    }

    /// <summary>Says <paramref name="line"/> on the status line of the run this flow belongs to; nothing outside a run. A
    /// status line that fails never fails the tool.</summary>
    internal static void Say(string line)
    {
        try { Current.Value?.Say(line); }
        catch (Exception ex) { Diagnostics.Swallow("ToolProgress.Say", ex); }
    }

    /// <summary>The status line of a step a tool played: its number from 1, how many, and what it did (said in English by the
    /// tool) — or why it was not done.</summary>
    internal static void Step(string tool, int step, int of, bool done, string detail) =>
        Say(done ? Localization.Strings.ToolStepProgress(tool, step, of, detail)
                 : Localization.Strings.ToolStepNotDone(tool, step, of, detail));

    private sealed class Scope(Action<string> onStep, Scope? previous) : IDisposable
    {
        private readonly object _gate = new();
        private Action<string>? _onStep = onStep;

        // Said under the lock: once Dispose returns, no line reaches the finished run (every sink only posts: the editor's
        // context, the host's notification).
        public void Say(string line)
        {
            lock (_gate) _onStep?.Invoke(line);
        }

        public void Dispose()
        {
            lock (_gate) _onStep = null;
            // Restored in the flow that routed: the one that disposes.
            if (Current.Value == this) Current.Value = previous;
        }
    }
}
