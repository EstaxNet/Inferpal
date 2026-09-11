using Inferpal.Localization;

namespace Inferpal.Services.Tools;

/// <summary>
/// Bookkeeping for the file-scan caps of the analysis tools (<c>analyze_impact</c>,
/// <c>analyze_code</c>, the cross-language nexus): how many files existed, how many were actually
/// read, and the warning line to append when the answer is only based on a sample.
/// </summary>
/// <remarks>
/// These tools answer questions like "what breaks if I change this file?". Truncating the scan is
/// fine — reading a 10 000-file repository on every call is not an option — but truncating it
/// <b>silently</b> is not: "Direct dependants (0)" then reads as "nothing depends on this" when it
/// really means "nothing among the arbitrary first 500 files the filesystem happened to enumerate".
/// The agent has no way to tell the two apart, and neither has the user. This is the same
/// discipline the agent loop already applies to oversized tool results
/// (<c>[... truncated to N characters out of M]</c>).
/// </remarks>
internal readonly record struct ScanCoverage(int Total, int Scanned)
{
    /// <summary>True when files were left out of the scan.</summary>
    public bool IsPartial => Total > Scanned;

    /// <summary>
    /// Takes at most <paramref name="cap"/> items and records how many there were in total.
    /// Enumerates <paramref name="files"/> once.
    /// </summary>
    public static (List<string> Files, ScanCoverage Coverage) Take(IEnumerable<string> files, int cap)
    {
        var all     = files as IList<string> ?? files.ToList();
        var scanned = all.Take(cap).ToList();
        return (scanned, new ScanCoverage(all.Count, scanned.Count));
    }

    /// <summary>The localized warning to append to a partial report, or an empty string.</summary>
    public string Warning() => IsPartial ? Strings.ScanPartial(Scanned, Total) : string.Empty;

    /// <summary>
    /// The more truncated of two scans. A tool whose sections do not all read the same subset must
    /// warn about the <b>worst</b> of them, not about whichever one it happened to measure last.
    /// </summary>
    /// <remarks>
    /// Written for <c>trace_dependency</c>, which scans twice — once for callers, once
    /// to build the callee definition index — and reported only the first. In
    /// <c>direction: "callees"</c> the caller scan never runs, so the coverage stayed
    /// <c>default</c> and <b>no warning was ever emitted</b> while the index had quietly skipped
    /// every file past the cap.
    /// </remarks>
    public static ScanCoverage Worst(ScanCoverage a, ScanCoverage b)
    {
        if (a.IsPartial && b.IsPartial) return a.Scanned <= b.Scanned ? a : b;
        if (a.IsPartial) return a;
        if (b.IsPartial) return b;
        // Neither is partial: keep the one that actually looked at something, so a section that
        // did not run (default) never displaces a real, complete scan.
        return a.Total >= b.Total ? a : b;
    }
}
