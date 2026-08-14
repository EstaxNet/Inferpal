using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Governance;

/// <summary>How much a reviewer should care.</summary>
internal enum CheckSeverity { Blocker, Warning, Nit }

/// <summary>Whether a finding's location survived being checked against the diff.</summary>
internal enum AnchorKind
{
    /// <summary>The line is inside a changed hunk — click and land on the code.</summary>
    Exact,
    /// <summary>The file is in the diff but the line was not; snapped to the nearest changed line.</summary>
    Adjusted,
    /// <summary>The file is not in the diff at all. Kept, but never presented as a location.</summary>
    Unanchored,
}

/// <param name="ReportedLine">What the model said, kept so an adjustment stays visible.</param>
internal sealed record CheckFinding(
    CheckSeverity Severity,
    string File,
    int Line,
    int ReportedLine,
    string Message,
    AnchorKind Anchor);

/// <param name="Prose">Everything the model wrote that is not a finding — never discarded.</param>
internal sealed record CheckReview(IReadOnlyList<CheckFinding> Findings, string Prose);

/// <summary>
/// Turns a review answer into findings anchored to the diff (roadmap §15).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why parse at all.</b> <c>/check</c> used to hand back a wall of prose: the reader had to
/// find "src/Foo.cs line 42" by eye and go there by hand, at the exact moment — just before a
/// commit — when friction costs the most.
/// </para>
/// <para>
/// <b>Why every location is verified.</b> A local model asked for <c>file:line</c> always produces
/// one; nothing makes it true. Each location is confronted with <see cref="DiffAnchors"/> and the
/// result is <i>labelled</i>: exact, adjusted, or unanchored. Silently dropping unanchored
/// findings would hide real remarks, and silently keeping them would present a guess as a
/// location — the same "plausible but wrong" failure the semantic index (§14) exists to remove.
/// </para>
/// </remarks>
internal static class CheckReviewParser
{
    // "- [blocker] path/to/file.cs:42 — message", and the shapes a model actually emits around it:
    // bullets or numbering, severity bracketed or not, path in backticks or quotes, any of the
    // usual separators before the message.
    private static readonly Regex FindingLine = new(
        """
        ^\s*(?:[-*•>]|\d+[.)])?\s*
        (?:[\[(]?\s*(?<sev>blocker|blocking|critical|error|warning|warn|nit|minor|info)\s*[\])]?\s*[:\-–—]?\s*)?
        \*{0,2}[`"']?(?<file>[A-Za-z0-9_./\\+-]+\.[A-Za-z0-9]+)[`"']?\*{0,2}
        \s*[:(]\s*(?:line\s*)?(?<line>\d+)\)?
        \s*[)\]]?\s*[:\-–—]?\s*
        (?<msg>.*)$
        """,
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);

    // Severity stated after the message ("… (blocker)") rather than before it.
    private static readonly Regex TrailingSeverity = new(
        @"[\[(]\s*(?<sev>blocker|blocking|critical|error|warning|warn|nit|minor|info)\s*[\])]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Splits <paramref name="modelOutput"/> into anchored findings and the remaining prose.
    /// </summary>
    public static CheckReview Parse(string? modelOutput, DiffAnchors anchors)
    {
        var findings = new List<CheckFinding>();
        var prose    = new StringBuilder();
        if (string.IsNullOrWhiteSpace(modelOutput)) return new CheckReview(findings, string.Empty);

        foreach (var raw in modelOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (FindingLine.Match(line) is not { Success: true } m)
            {
                prose.AppendLine(line);
                continue;
            }

            var message = m.Groups["msg"].Value.Trim();
            var severity = ParseSeverity(
                m.Groups["sev"].Success ? m.Groups["sev"].Value
                : TrailingSeverity.Match(message) is { Success: true } t ? t.Groups["sev"].Value
                : null);
            message = TrailingSeverity.Replace(message, "").TrimEnd();

            // A location with nothing said about it is not a finding — most often it is a plain
            // prose mention of a file, which belongs in the prose.
            if (message.Length == 0) { prose.AppendLine(line); continue; }

            var file = m.Groups["file"].Value.Replace('\\', '/');
            int reported = int.Parse(m.Groups["line"].Value);

            var (anchor, resolved) =
                anchors.Covers(file, reported)         ? (AnchorKind.Exact, reported)
                : anchors.Nearest(file, reported) is { } near ? (AnchorKind.Adjusted, near)
                : (AnchorKind.Unanchored, reported);

            findings.Add(new CheckFinding(severity, file, resolved, reported, message, anchor));
        }

        return new CheckReview(findings, prose.ToString().Trim());
    }

    private static CheckSeverity ParseSeverity(string? word) => word?.ToLowerInvariant() switch
    {
        "blocker" or "blocking" or "critical" or "error" => CheckSeverity.Blocker,
        "nit" or "minor" or "info"                       => CheckSeverity.Nit,
        _                                                 => CheckSeverity.Warning,
    };

    /// <summary>
    /// Markdown for the chat: findings grouped by file and ordered by severity then line, so the
    /// reading order is the order you would fix them in. Prose is kept above, verbatim.
    /// </summary>
    public static string Render(CheckReview review)
    {
        var sb = new StringBuilder();
        if (review.Prose.Length > 0) sb.Append(review.Prose).Append("\n\n");

        if (review.Findings.Count == 0)
        {
            sb.Append(Strings.CheckNoFindings);
            return sb.ToString().Trim();
        }

        sb.Append(Strings.CheckFindingsHeader(review.Findings.Count)).Append("\n\n");

        var byFile = review.Findings
            .GroupBy(f => f.File, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Min(f => (int)f.Severity))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byFile)
        {
            sb.Append("**").Append(group.Key).Append("**\n");
            foreach (var f in group.OrderBy(f => (int)f.Severity).ThenBy(f => f.Line))
            {
                sb.Append("- `").Append(f.File).Append(':').Append(f.Line).Append("` ")
                  .Append(Label(f.Severity)).Append(" — ").Append(f.Message);

                if (f.Anchor == AnchorKind.Adjusted)
                    sb.Append("  _(").Append(Strings.CheckAnchorAdjusted(f.ReportedLine)).Append(")_");
                else if (f.Anchor == AnchorKind.Unanchored)
                    sb.Append("  _(").Append(Strings.CheckAnchorUnanchored).Append(")_");

                sb.Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static string Label(CheckSeverity severity) => severity switch
    {
        CheckSeverity.Blocker => Strings.CheckSeverityBlocker,
        CheckSeverity.Nit     => Strings.CheckSeverityNit,
        _                     => Strings.CheckSeverityWarning,
    };
}
