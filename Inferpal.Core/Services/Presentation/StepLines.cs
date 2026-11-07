using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

/// <summary>
/// The step lines that are said again in place while one thing goes on: a prefill wait (<c>ModelWait.Short</c>, every
/// beat) and a compaction summary's progress (<c>SummaryFuse</c>). A front-end's status line replaces itself; a journal
/// of steps — a <c>/task</c>'s, whose run has no wait scope — keeps one entry for them, or a slow read counts a step
/// every five seconds.
/// </summary>
internal static class StepLines
{
    /// <summary>Whether <paramref name="next"/> says again what <paramref name="previous"/> said: the same wait, or the
    /// same compaction, a moment later.</summary>
    public static bool SaysAgain(string previous, string next) =>
        (IsCompacting(previous) && IsCompacting(next)) || (IsWait(previous) && IsWait(next));

    /// <summary>"Compacting context…", alone or with its progress, which begins with it in every language.</summary>
    private static bool IsCompacting(string line) => line.StartsWith(Strings.StatusCompacting, StringComparison.Ordinal);

    /// <summary>A prefill wait's short line: one of its titles, then its times.</summary>
    private static bool IsWait(string line) =>
        line.StartsWith(Strings.WaitReading + " · ", StringComparison.Ordinal)
        || line.StartsWith(Strings.WaitReadingLonger + " · ", StringComparison.Ordinal)
        || line.StartsWith(Strings.WaitLoadingModel + " · ", StringComparison.Ordinal);
}
