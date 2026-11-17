using System.Globalization;
using System.Text;

namespace Inferpal.Services.Shell;

/// <summary>One run of a repeated command: its time and how it ended.</summary>
internal readonly record struct RunTiming(double Seconds, int? ExitCode);

/// <summary>The times of one repeated measure: what a comparison needs.</summary>
internal sealed record TimingSummary(double Median, double Min, double Max, double Iqr, int Runs);

/// <summary>
/// A command run several times, timed (pari 103): <c>run_command</c> with <c>repeat</c>. One number says nothing — the second
/// run of a .NET program is often faster than the first (JIT, disk cache), and 4 % is within the noise — so the answer gives the
/// median and the spread of the runs after a warm-up, and, when the same command was measured before in the session, whether
/// the difference is beyond the noise.
/// </summary>
/// <remarks>
/// ⚠ The verdict is a sentence, not two numbers to compare: given one run before and one after, a model keeps a change that
/// gains nothing (a 4 % "gain"); told the runs overlap, it reverts it.
/// ⚠ A failed run is not a time: it is left out of the figures and counted; all failed, there is no figure at all.
/// </remarks>
internal static class RepeatedRun
{
    // ⚠ Two ranges of pure noise fail to overlap with probability 2·n!²/(2n)! — 1 in 3 at two runs, 1 in 126 at five: under
    // five, "beyond the noise" is a coin's verdict.
    internal const int MinRepeat = 5, MaxRepeat = 20, DefaultRepeat = 5;

    /// <summary>The fewest good runs on each side for a verdict on the noise.</summary>
    internal const int MinRunsToCompare = 3;

    /// <summary>The figures of the runs that ended well (exit code 0), or <c>null</c> when none did.</summary>
    internal static TimingSummary? Summarize(IReadOnlyList<RunTiming> runs)
    {
        var ok = runs.Where(r => r.ExitCode == 0).Select(r => r.Seconds).OrderBy(s => s).ToList();
        if (ok.Count == 0) return null;
        return new(Quantile(ok, 0.5), ok[0], ok[^1], Quantile(ok, 0.75) - Quantile(ok, 0.25), ok.Count);
    }

    /// <summary>The report: each run's time, the figures, and the comparison with <paramref name="previous"/> (the same
    /// command's last measure in the session) when there is one.</summary>
    internal static string Report(IReadOnlyList<RunTiming> runs, double? warmUp, TimingSummary? previous)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        if (warmUp is { } w) sb.AppendLine(string.Format(inv, "warm-up run (not counted): {0:0.000} s", w));
        sb.AppendLine("runs (each includes starting the shell, the same for every run): " + string.Join(", ", runs.Select(r => string.Format(inv, "{0:0.000} s", r.Seconds)
                                                                    + (r.ExitCode == 0 ? "" : $" (exit {r.ExitCode?.ToString(inv) ?? "unknown"})"))));
        var failed = runs.Count(r => r.ExitCode != 0);
        var now = Summarize(runs);
        if (now is null)
        {
            sb.Append($"no time: all {runs.Count} runs failed — a failed run is not a time; fix the command first.");
            return sb.ToString();
        }
        sb.AppendLine(string.Format(inv, "median {0:0.000} s, min {1:0.000}, max {2:0.000}, IQR {3:0.000} ({4} runs{5})",
                                    now.Median, now.Min, now.Max, now.Iqr, now.Runs,
                                    failed > 0 ? $"; {failed} failed run(s) left out" : ""));
        sb.Append(previous is null
            ? "first measure of this command in the session: measure it again after a change to compare."
            : previous.Runs < MinRunsToCompare || now.Runs < MinRunsToCompare
                ? $"too few good runs to compare with the previous measure of this command (at least {MinRunsToCompare} on each side)."
                : Compare(previous, now));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Whether <paramref name="after"/> differs from <paramref name="before"/> beyond the noise: their ranges of runs
    /// do not overlap.</summary>
    internal static string Compare(TimingSummary before, TimingSummary after)
    {
        var inv = CultureInfo.InvariantCulture;
        var delta = (after.Median - before.Median) / before.Median * 100;
        var head = string.Format(inv, "against the previous measure of this command (median {0:0.000} s, {1} runs): {2:+0.0;-0.0} % on the median — ",
                                 before.Median, before.Runs, delta);
        var overlap = before.Max >= after.Min && after.Max >= before.Min;
        return head + (overlap
            ? "the ranges of the runs overlap: the difference is within the noise, not a gain or a loss."
            : (delta < 0 ? "the ranges do not overlap: faster, beyond the noise." : "the ranges do not overlap: slower, beyond the noise."));
    }

    private static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 1) return sorted[0];
        var at = q * (sorted.Count - 1);
        var low = (int)Math.Floor(at);
        var high = Math.Min(low + 1, sorted.Count - 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (at - low);
    }
}
