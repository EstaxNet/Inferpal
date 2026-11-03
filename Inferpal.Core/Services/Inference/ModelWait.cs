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
/// <param name="Loading">The server said the model is not in memory: it loads it before it reads anything. ⚠ Named as
/// such: without it the morning's first question reads "Reading the prompt — longer than usual" during a load, which
/// sends the person to their prompt, and the measured read is no measure of a load.</param>
/// <param name="ReadFrom">When the read began, within <paramref name="Elapsed"/>: the end of a load the wait saw, zero
/// otherwise. ⚠ The read is judged from there: counted from the request, a 20 s load makes a 6 s read "longer than
/// usual" the moment the model is in memory.</param>
internal sealed record ModelWait(int Tokens, TimeSpan Elapsed, TimeSpan? ColdRead, TimeSpan Bound, bool Loading = false,
                                 TimeSpan ReadFrom = default)
{
    /// <summary>The cold read when it says something: under a second it measured nothing a person can read ("0 s").</summary>
    private TimeSpan? Measured => ColdRead is { } cold && cold >= TimeSpan.FromSeconds(1) ? cold : null;

    /// <summary>The time the server has been reading, a load it did first left out.</summary>
    private TimeSpan Read => Elapsed - ReadFrom;

    /// <summary>Past its measured cold read: the server is slower than its own record, and the bound is what counts.</summary>
    public bool Longer => !Loading && Measured is { } cold && Read > cold;

    /// <summary>How far along, 0 to 1 — against the cold read, then against the bound; <c>null</c> with nothing measured,
    /// where a bar would only creep toward the bound and read as a forecast — a load included.</summary>
    public double? Fraction => Loading ? null
                             : Longer ? Math.Min(1, Elapsed / Bound)
                             : Measured is { } cold ? Math.Min(1, Read / cold)
                             : null;

    public string Title => Loading ? Strings.WaitLoadingModel : Longer ? Strings.WaitReadingLonger : Strings.WaitReading;

    /// <summary>The time waited, against what it is measured by.</summary>
    /// <remarks>⚠ Past the bound, the time alone: a server that sends a sign of life (headers, a keep-alive) restarts the
    /// silence it is judged on, and "3 min of 2 min" would read as a broken promise.</remarks>
    public string Times => Loading ? RunSummary.Duration(Elapsed)
                         : Longer && Elapsed <= Bound ? Strings.WaitTimesOfBound(RunSummary.Duration(Elapsed), RunSummary.Duration(Bound))
                         : !Longer && Measured is { } cold ? Strings.WaitTimesOfAbout(RunSummary.Duration(Read), RunSummary.Duration(cold))
                         : RunSummary.Duration(Elapsed);

    /// <summary>The tokens, the measured read, and when the wait stops — said from the start, never only at the cut.</summary>
    public string Detail => Loading ? Strings.WaitDetailLoading(Tokens, RunSummary.Duration(Bound))
                          : Longer ? Strings.WaitDetailLonger(RunSummary.Duration(Measured!.Value), RunSummary.Duration(Bound))
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
