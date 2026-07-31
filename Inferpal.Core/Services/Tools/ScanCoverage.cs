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
}
