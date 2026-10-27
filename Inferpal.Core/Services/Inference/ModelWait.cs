using Inferpal.Localization;

namespace Inferpal.Services.Inference;

/// <summary>
/// A request waiting on a server that reads its prompt and sends nothing: the tokens to read, the time waited, the
/// measured cold read when there is one, and the bound at which the wait stops — what every front-end draws, in the
/// same words.
/// </summary>
/// <param name="ColdRead">The measured cold read of a request this size: an upper bound (what is cached is read at once);
/// <c>null</c> when not measured.</param>
/// <param name="Bound">When the request is given up if the server stays silent (<see cref="PrefillDeadline"/>).</param>
internal sealed record ModelWait(int Tokens, TimeSpan Elapsed, TimeSpan? ColdRead, TimeSpan Bound)
{
    /// <summary>The cold read when it says something: under a second it measured nothing a person can read ("0 s").</summary>
    private TimeSpan? Measured => ColdRead is { } cold && cold >= TimeSpan.FromSeconds(1) ? cold : null;

    /// <summary>Past its measured cold read: the server is slower than its own record, and the bound is what counts.</summary>
    public bool Longer => Measured is { } cold && Elapsed > cold;

    /// <summary>How far along, 0 to 1 — against the cold read, then against the bound; <c>null</c> with nothing measured,
    /// where a bar would only creep toward the bound and read as a forecast.</summary>
    public double? Fraction => Longer ? Math.Min(1, Elapsed / Bound)
                             : Measured is { } cold ? Math.Min(1, Elapsed / cold)
                             : null;

    public string Title => Longer ? Strings.WaitReadingLonger : Strings.WaitReading;

    /// <summary>The time waited, against what it is measured by.</summary>
    /// <remarks>⚠ Past the bound, the time alone: a server that sends a sign of life (headers, a keep-alive) restarts the
    /// silence it is judged on, and "3 min of 2 min" would read as a broken promise.</remarks>
    public string Times => Longer && Elapsed <= Bound ? Strings.WaitTimesOfBound(RunSummary.Duration(Elapsed), RunSummary.Duration(Bound))
                         : !Longer && Measured is { } cold ? Strings.WaitTimesOfAbout(RunSummary.Duration(Elapsed), RunSummary.Duration(cold))
                         : RunSummary.Duration(Elapsed);

    /// <summary>The tokens, the measured read, and when the wait stops — said from the start, never only at the cut.</summary>
    public string Detail => Longer ? Strings.WaitDetailLonger(RunSummary.Duration(Measured!.Value), RunSummary.Duration(Bound))
                          : Measured is { } cold ? Strings.WaitDetail(Tokens, RunSummary.Duration(cold), RunSummary.Duration(Bound))
                          : Strings.WaitDetailUnmeasured(Tokens, RunSummary.Duration(Bound));

    /// <summary>The detail for a surface with no Stop button (Visual Studio's in-place window, VS Code's progress
    /// notification): the longer wait does not name a gesture that is not there.</summary>
    public string DetailWithoutStop => Longer
        ? Strings.WaitDetailLongerNoStop(RunSummary.Duration(Measured!.Value), RunSummary.Duration(Bound))
        : Detail;

    /// <summary>The title and the times, for a step line or a label that has no room for the rest.</summary>
    public string Short => $"{Title} · {Times}";
}
