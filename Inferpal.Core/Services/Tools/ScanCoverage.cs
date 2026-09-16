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
/// <param name="Total">How many files the scan could have looked at.</param>
/// <param name="Scanned">How many the cap let through — <b>taken</b>, not necessarily read.</param>
/// <param name="Unreadable">
/// How many of those <paramref name="Scanned"/> files could not be read after all.
/// </param>
/// <remarks>
/// ⚠ <b>A file that was taken and then failed to READ is the same silence as the cap, and this
/// struct had no room for it.</b> Its own summary said <i>"how many were actually read"</i> while
/// <see cref="Take"/> can only report how many were taken — so a locked, permission-denied or
/// just-deleted file was counted as scanned, <see cref="IsPartial"/> stayed <c>false</c>, and the
/// report read as complete. Measured on <c>analyze_impact</c>: with the one dependant of a file
/// unreadable, it answered <c>Direct dependants (0) · Risk: LOW · No dependants detected — safe to
/// refactor freely</c>, which is the exact sentence this tool's own comment records as the defect
/// it once had for a different cause.
/// </remarks>
internal readonly record struct ScanCoverage(int Total, int Scanned, int Unreadable = 0)
{
    /// <summary>True when the <b>cap</b> left files out.</summary>
    public bool IsPartial => Total > Scanned;

    /// <summary>
    /// True when the scan did not see the whole set, for <b>either</b> reason — the cap, or a file
    /// it could not read.
    /// </summary>
    /// <remarks>
    /// ⚠ This is the question every reader actually asks, and it is the one the type must answer:
    /// the five reading sites each wrote their own gate (<c>if (coverage.IsPartial)</c>) around
    /// <see cref="Warning"/>, so a second cause of incompleteness had to be remembered five times.
    /// A property the callers cannot forget beats a rule they must apply.
    /// </remarks>
    public bool IsIncomplete => IsPartial || Unreadable > 0;

    /// <summary>The same coverage, plus <paramref name="count"/> files that could not be read.</summary>
    /// <remarks>Additive, so a tool whose scan runs in several loops can call it per loop.</remarks>
    public ScanCoverage WithUnreadable(int count) =>
        count <= 0 ? this : this with { Unreadable = Unreadable + count };

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

    /// <summary>
    /// The localized warning to append to an incomplete report, or an empty string.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Two causes, two sentences.</b> "Only 400 of 652 files were scanned (cap)" and "3 files
    /// could not be read" send the reader to two different places — one is a budget to narrow, the
    /// other is a lock or a permission to fix — and folding the second into the first (which
    /// <c>rename_symbol</c> did, by subtracting its unreadable count from <c>Scanned</c>) names the
    /// wrong cause. Both are emitted when both happened.
    /// </remarks>
    public string Warning()
    {
        var cap = IsPartial ? Strings.ScanPartial(Scanned, Total) : string.Empty;
        if (Unreadable <= 0) return cap;
        var unread = Strings.ScanUnreadable(Unreadable);
        return cap.Length == 0 ? unread : cap + "\n" + unread;
    }

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
    /// <remarks>
    /// ⚠ <b>The unreadable count follows the chosen scan, as the MAXIMUM of the two — never the
    /// sum.</b> The two scans this was written for walk the <b>same</b> tree, so the same locked
    /// file is seen by both and adding them would double it. A tool whose scans cover
    /// <b>disjoint</b> trees (the cross-language nexus: C# then TypeScript) adds its coverages
    /// itself, and there the sum is the right answer. Carried here rather than in a second method:
    /// this one already has the only caller that combines two scans, and a near-twin name is one
    /// more thing to pick wrongly.
    /// </remarks>
    public static ScanCoverage Worst(ScanCoverage a, ScanCoverage b)
    {
        var worst = Pick(a, b);
        return worst with { Unreadable = Math.Max(a.Unreadable, b.Unreadable) };
    }

    private static ScanCoverage Pick(ScanCoverage a, ScanCoverage b)
    {
        if (a.IsPartial && b.IsPartial) return a.Scanned <= b.Scanned ? a : b;
        if (a.IsPartial) return a;
        if (b.IsPartial) return b;
        // Neither is partial: keep the one that actually looked at something, so a section that
        // did not run (default) never displaces a real, complete scan.
        return a.Total >= b.Total ? a : b;
    }

}
