namespace Inferpal.Services.Agent;

/// <summary>
/// A chat run that reads its own diff back before it answers (pari 159), as the agent loop does: asked for by the chat
/// turn of each front-end, which hands the loop the way to empty the answer it has streamed so far.
/// </summary>
/// <remarks>
/// ⚠ Opt-in, by scope: the basic loop also runs <c>/tdd</c>'s fixes, <c>/commit</c>, <c>/pr</c> and the recap, none of which
/// streams an answer to a bubble a review could empty. Without the reset, the answer held for the review would stay on screen
/// above the one written after it.
/// </remarks>
internal static class RunDiffReview
{
    private static readonly AsyncLocal<Action?> Reset = new();

    /// <summary>The reset of the chat turn that asked for a review in this flow; <c>null</c> when none did.</summary>
    internal static Action? Current => Reset.Value;

    /// <summary>Asks the runs of this flow to review their diff, <paramref name="onStreamReset"/> emptying the streamed answer.</summary>
    internal static IDisposable Enter(Action onStreamReset)
    {
        var before = Reset.Value;
        Reset.Value = onStreamReset;
        return new Scope(before);
    }

    private sealed class Scope(Action? before) : IDisposable
    {
        public void Dispose() => Reset.Value = before;
    }
}
