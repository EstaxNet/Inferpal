namespace Inferpal.Services.Debugging;

/// <summary>
/// Bounds how far one debugging session may be driven, and — the part that matters — makes
/// exhaustion <b>reportable</b> rather than silent.
/// </summary>
/// <remarks>
/// Straight from the §20 post-mortem, which is the only reason this class exists. There, sub-agents
/// that ran out of room concluded anyway, and the parent inherited a belief instead of a result.
/// A debugging loop can fail the same way: forty steps into the wrong branch, a model that has run
/// out of budget must say "I did not conclude", never invent the answer it was hunting for.
/// Consumption is therefore counted here, and the exhaustion message is written once, in English,
/// as an instruction to the model.
/// </remarks>
internal sealed class DebugStepBudget(int max = DebugStepBudget.DefaultMax)
{
    internal const int DefaultMax = 40;

    private int _used;

    internal int Max { get; } = max > 0 ? max : DefaultMax;

    internal int Used => _used;

    internal int Remaining => Math.Max(0, Max - _used);

    internal bool IsExhausted => Remaining == 0;

    /// <summary>Takes one step from the budget. <c>false</c> when there was none left.</summary>
    internal bool TryConsume()
    {
        if (IsExhausted) return false;
        _used++;
        return true;
    }

    /// <summary>Starts the count again — a new session gets a full budget.</summary>
    internal void Reset() => _used = 0;

    /// <summary>
    /// What the model is told when the budget runs out. Model-facing English, and deliberately an
    /// instruction rather than a mere fact: "no budget left" alone invites a confident guess.
    /// </summary>
    internal string ExhaustedMessage =>
        $"Step budget exhausted ({Max} steps used in this debugging session). Do not step further and "
      + "do not guess the outcome you were looking for. Report what you established, and state "
      + "explicitly that you did not reach a conclusion. The user can restart a session to continue.";

    /// <summary>Appended to a step result so the model can pace itself before hitting the wall.</summary>
    internal string Trailer => $" [{Remaining}/{Max} steps left]";
}
