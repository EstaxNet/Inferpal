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
/// really means "nothing among the first 500 files the filesystem happened to enumerate". The agent
/// has no way to tell the two apart, and neither has the user. Same discipline as the agent loop's
/// oversized tool results (<c>[... truncated to N characters out of M]</c>).
/// </remarks>
/// <param name="Total">How many files the scan could have looked at.</param>
/// <param name="Scanned">How many the cap let through — <b>taken</b>, not necessarily read.</param>
/// <param name="Unreadable">
/// How many of those <paramref name="Scanned"/> files could not be read after all.
/// </param>
/// <remarks>
/// ⚠ <b>A file that was taken and then failed to read is the same silence as the cap.</b> A locked,
/// permission-denied or just-deleted file used to be counted as scanned, <see cref="IsPartial"/>
/// stayed <c>false</c>, and the report read as complete — so <c>analyze_impact</c> could answer
/// <c>Direct dependants (0) · Risk: LOW · No dependants detected — safe to refactor freely</c> with
/// the one dependant simply unreadable.
/// </remarks>
/// <param name="Gap">
/// A folder of the workspace whose files the walk will not see, with the reason — <c>null</c> when
/// the whole tree is readable and unlinked. See <see cref="WithGap"/>.
/// </param>
internal readonly record struct ScanCoverage(
    int Total, int Scanned, int Unreadable = 0, WorkspaceScan.WalkGap? Gap = null)
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
    public bool IsIncomplete => IsPartial || Unreadable > 0 || Gap is not null;

    /// <summary>The same coverage, plus <paramref name="count"/> files that could not be read.</summary>
    /// <remarks>Additive, so a tool whose scan runs in several loops can call it per loop.</remarks>
    public ScanCoverage WithUnreadable(int count) =>
        count <= 0 ? this : this with { Unreadable = Unreadable + count };

    /// <summary>
    /// The same coverage, plus a folder the walk could not list.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A folder that cannot be listed is one step worse than a file that cannot be read, and
    /// <see cref="Unreadable"/> cannot express it</b>: the files under it were never enumerated, so
    /// they are absent from <see cref="Total"/> as well — the arithmetic is self-consistent and
    /// claims a complete scan. It therefore needs its own member and its own sentence, and it names
    /// the folder rather than counting files, because the number of files inside is exactly what
    /// nobody can know. The walk skips such folders in silence
    /// (<c>EnumerationOptions.IgnoreInaccessible</c>), and the funnel's <c>failed</c> flag only ever
    /// meant "the START directory could not be opened".
    /// </remarks>
    public ScanCoverage WithGap(WorkspaceScan.WalkGap? gap) =>
        gap is null ? this : this with { Gap = gap };

    /// <summary>
    /// Takes at most <paramref name="cap"/> items, in a defined order, and records how many there
    /// were in total. Enumerates <paramref name="files"/> once.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The cap declares itself; the SUBSET decided nowhere.</b> The report says "400 of 652",
    /// and what it cannot say is that those 400 are the ones the volume happened to yield first:
    /// <c>Directory.GetFiles</c> and <c>FileSystemEnumerable</c> return entries in file-system
    /// order — by name on NTFS, <b>arbitrary</b> on POSIX. So on a repository larger than the cap,
    /// <c>analyze_impact</c>, <c>trace_dependency</c> and the nexus could answer two different
    /// things to two identical calls, neither of them reproducible. Sorted here, once, for every
    /// site that caps: ordinal, so the machine's culture does not decide either (rule 19).
    /// ⚠ The order must be fixed <b>before</b> the cap, not after — sorting what was already taken
    /// tidies the report and changes nothing about which files were read.
    /// </remarks>
    public static (List<string> Files, ScanCoverage Coverage) Take(IEnumerable<string> files, int cap)
    {
        var all     = files as IList<string> ?? files.ToList();
        var scanned = all.OrderBy(f => f, StringComparer.Ordinal).Take(cap).ToList();
        return (scanned, new ScanCoverage(all.Count, scanned.Count));
    }

    /// <summary>
    /// The localized warning to append to an incomplete report, or an empty string.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Two causes, two sentences.</b> "Only 400 of 652 files were scanned (cap)" and "3 files
    /// could not be read" send the reader to two different places — one is a budget to narrow, the
    /// other a lock or a permission to fix — and folding the second into the first names the wrong
    /// cause. Both are emitted when both happened.
    /// </remarks>
    public string Warning()
    {
        var lines = new List<string>(3);
        if (IsPartial) lines.Add(Strings.ScanPartial(Scanned, Total));
        if (Unreadable > 0) lines.Add(Strings.ScanUnreadable(Unreadable));
        if (Gap is { } gap) lines.Add(gap.Sentence());
        return string.Join("\n", lines);
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
        return worst with
        {
            Unreadable       = Math.Max(a.Unreadable, b.Unreadable),
            // Either scan having seen it is enough: they walk the same tree, so the folder is the
            // same folder, and losing the mention because the other scan happened to be "worse"
            // would be the silence this member exists to end.
            Gap              = a.Gap ?? b.Gap,
        };
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
