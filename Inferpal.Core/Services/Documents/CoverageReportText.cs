using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Inferpal.Services.Coverage;

namespace Inferpal.Services.Documents;

/// <summary>
/// A coverage report a test run wrote — lcov (<c>lcov.info</c>), Cobertura (<c>coverage.cobertura.xml</c>), JaCoCo
/// (<c>jacoco.xml</c>) — read as what someone writing the next test wants: per source file, how much is covered and which
/// lines are not, the least covered first and the file open in the editor before every other. Read through the model
/// <c>/test coverage</c> reads (<see cref="CoberturaReport"/>): one notion of a workspace's source file, tests,
/// generated and third-party code left out and counted.
/// </summary>
/// <remarks>⚠ Recognised by its content, never its name alone. ⚠ Never throws for a file of an unexpected shape: it is read
/// as its lines (<c>null</c>). The file stays a text file: asked for lines, each is the text it holds.</remarks>
internal static class CoverageReportText
{
    /// <summary>The largest report read this way (as SARIF): past it, its text is paged as it is.</summary>
    private const int MaxChars = 16_000_000;

    /// <summary>The most uncovered ranges said for one file; past it, the rest is counted.</summary>
    internal const int MaxRanges = 40;

    private enum Format { None, Lcov, Cobertura, Jacoco }

    private static Format Detect(string name, string text)
    {
        var lower = name.ToLowerInvariant();
        var head = text.Length > 4096 ? text[..4096] : text;
        if (head.StartsWith('\uFEFF')) head = head[1..];
        if ((lower.EndsWith(".info", StringComparison.Ordinal) || lower.EndsWith(".lcov", StringComparison.Ordinal) || lower.Contains("lcov", StringComparison.Ordinal))
            && (head.StartsWith("TN:", StringComparison.Ordinal) || head.StartsWith("SF:", StringComparison.Ordinal) || head.Contains("\nSF:", StringComparison.Ordinal)))
            return Format.Lcov;
        if (!lower.EndsWith(".xml", StringComparison.Ordinal)) return Format.None;
        if (head.Contains("<coverage", StringComparison.Ordinal) && (head.Contains("line-rate", StringComparison.Ordinal) || head.Contains("<packages", StringComparison.Ordinal)))
            return Format.Cobertura;
        if (head.Contains("<report", StringComparison.Ordinal) && (head.Contains("JACOCO", StringComparison.OrdinalIgnoreCase) || head.Contains("<sessioninfo", StringComparison.Ordinal)))
            return Format.Jacoco;
        return Format.None;
    }

    /// <summary>Whether <paramref name="text"/>, read from <paramref name="name"/>, is a coverage report this reads.</summary>
    internal static bool Handles(string name, string text) => text.Length <= MaxChars && Detect(name, text) != Format.None;

    /// <summary>A coverage report's uncovered lines, <paramref name="text"/> read from <paramref name="name"/>, within
    /// <paramref name="budget"/> characters; <c>null</c> for any other file.</summary>
    /// <param name="root">The workspace root the report's paths are read against.</param>
    /// <param name="activeFile">The file open in the editor, said first; <c>null</c> when none.</param>
    /// <param name="exists">Whether a candidate path is a file (tests give a fixed answer).</param>
    /// <param name="reportFolder">The folder the report was read from: lcov and JaCoCo write paths relative to the project
    /// that ran the tests, which may be a module below the workspace root; <c>null</c> (an archive entry): the root only.</param>
    /// <param name="ct">Stops a long report.</param>
    internal static string? Summarize(string name, string text, int budget, string? root, string? activeFile = null,
                                      Func<string, bool>? exists = null, string? reportFolder = null, CancellationToken ct = default)
    {
        if (text.Length > MaxChars || string.IsNullOrEmpty(root)) return null;
        var format = Detect(name, text);
        if (format == Format.None) return null;
        exists ??= File.Exists;
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var bases = Bases(fullRoot, reportFolder);
            // Counted by name: Cobertura writes one class per type, lcov may repeat a file across test sections.
            var leftOut = new HashSet<string>(PathComparer.Default);
            var report = format switch
            {
                Format.Lcov => Lcov(text, fullRoot, bases, exists, n => leftOut.Add(n), ct),
                Format.Jacoco => Jacoco(text, fullRoot, bases, exists, n => leftOut.Add(n), ct),
                _ => CoberturaReport.Parse(text, fullRoot, exists, n => leftOut.Add(n), bases, ct),
            };
            if (report.Files.Count == 0 && leftOut.Count == 0) return null;
            var active = activeFile is null ? null : CoberturaReport.SourceOf(Path.GetFullPath(activeFile), fullRoot);
            // The report's own spelling of the open file: the editor and the tool may differ in case only.
            if (active is not null) active = report.Files.Keys.FirstOrDefault(k => PathComparer.Default.Equals(k, active)) ?? active;
            return Render(name, format, report, leftOut.Count, active, budget);
        }
        catch (Exception ex) when (ex is XmlException or FormatException or ArgumentException or InvalidOperationException
                                       or IOException or NotSupportedException or OverflowException or RegexMatchTimeoutException)
        {
            Diagnostics.Swallow("CoverageReportText.Summarize", ex);
            return null;
        }
    }

    /// <summary>The folders a relative path of the report is tried against: the report's folder and each parent up to the
    /// workspace root, then the root.</summary>
    private static List<string> Bases(string fullRoot, string? reportFolder)
    {
        var bases = new List<string>();
        if (reportFolder is not null)
            for (var dir = Path.GetFullPath(reportFolder); dir is not null && Tools.PathSanitizer.IsUnderRoot(dir, fullRoot)
                 && !PathComparer.Default.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar));
                 dir = Path.GetDirectoryName(dir))
                bases.Add(dir);
        bases.Add(fullRoot);
        return bases;
    }

    /// <summary>Where a relative <paramref name="path"/> of the report is: the first base where it exists, else <c>null</c>
    /// — a file found nowhere is not listed as if it were this workspace's.</summary>
    private static string? Find(string path, List<string> bases, Func<string, bool> exists)
    {
        if (Path.IsPathFullyQualified(path)) return CoberturaReport.Probe(Path.GetFullPath(path), bases[^1], exists) ? Path.GetFullPath(path) : null;
        foreach (var dir in bases)
        {
            var full = Path.GetFullPath(Path.Combine(dir, path));
            if (CoberturaReport.Probe(full, bases[^1], exists)) return full;
        }
        return null;
    }

    /// <summary>lcov: <c>SF:</c> a file, <c>DA:line,hits</c> its lines, <c>end_of_record</c>.</summary>
    private static CoberturaReport Lcov(string text, string fullRoot, List<string> bases, Func<string, bool> exists, Action<string> leftOut,
                                        CancellationToken ct)
    {
        var files = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        Dictionary<int, int>? lines = null;
        var n = 0;
        foreach (var raw in text.Split('\n'))
        {
            if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested();
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("SF:", StringComparison.Ordinal))
            {
                // Either separator, as the Cobertura reader takes it: a report written under Windows is read anywhere.
                var path = line[3..].Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                if (Find(path, bases, exists) is { } full && CoberturaReport.SourceOf(full, fullRoot) is { } relative)
                {
                    if (!files.TryGetValue(relative, out lines)) files[relative] = lines = [];
                }
                else { lines = null; leftOut(path); }
            }
            else if (line.StartsWith("DA:", StringComparison.Ordinal) && lines is not null)
            {
                var parts = line[3..].Split(',');
                if (parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
                    && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hits))
                    lines[number] = Math.Max(lines.GetValueOrDefault(number), (int)Math.Clamp(hits, 0, int.MaxValue));
            }
            else if (line == "end_of_record") lines = null;
        }
        return CoberturaReport.FromLines(files);
    }

    /// <summary>The folders a JaCoCo report's package paths are written from: its <c>sourcefile</c> names a package path,
    /// not where the source lives.</summary>
    private static readonly string[] JavaSourceRoots = ["src/main/java", "src/main/kotlin", "src", ""];

    /// <summary>JaCoCo: <c>package name="com/acme"</c> → <c>sourcefile name="Cart.java"</c> → <c>line nr mi ci</c>.</summary>
    private static CoberturaReport Jacoco(string xml, string fullRoot, List<string> bases, Func<string, bool> exists, Action<string> leftOut,
                                          CancellationToken ct)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var doc = XDocument.Load(reader);
        var files = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        foreach (var package in doc.Descendants("package"))
            foreach (var source in package.Elements("sourcefile"))
            {
                ct.ThrowIfCancellationRequested();
                var name = ((string?)package.Attribute("name") is { Length: > 0 } pkg ? pkg + "/" : string.Empty) + (string?)source.Attribute("name");
                // A module's report sits in <module>/target/site/jacoco or <module>/build/reports/jacoco: its sources are
                // under one of the report's parents.
                var full = JavaSourceRoots.Select(r => Find(Path.Combine(r, name), bases, exists)).FirstOrDefault(f => f is not null);
                if (full is null || CoberturaReport.SourceOf(full, fullRoot) is not { } relative) { leftOut(name); continue; }
                if (!files.TryGetValue(relative, out var lines)) files[relative] = lines = [];
                foreach (var line in source.Elements("line"))
                {
                    if (!int.TryParse((string?)line.Attribute("nr"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;
                    if (number <= 0) continue;
                    // A line with a covered instruction is covered; missed instructions alone, it is not.
                    var covered = ((int?)line.Attribute("ci") ?? 0) > 0 ? 1 : 0;
                    lines[number] = Math.Max(lines.GetValueOrDefault(number), covered);
                }
            }
        return CoberturaReport.FromLines(files);
    }

    private static string Percent(int covered, int total) =>
        total == 0 ? "no line" : (100.0 * covered / total).ToString("0.0", CultureInfo.InvariantCulture) + " %";

    /// <summary>"5-9, 14, 22-30": consecutive lines as one range.</summary>
    internal static List<string> Ranges(IEnumerable<int> lines)
    {
        var ranges = new List<string>();
        int? start = null, end = null;
        foreach (var n in lines.Order())
        {
            if (end is { } e && n == e + 1) { end = n; continue; }
            if (start is { } s) ranges.Add(s == end ? s.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{s}-{end}"));
            start = end = n;
        }
        if (start is { } last) ranges.Add(last == end ? last.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{last}-{end}"));
        return ranges;
    }

    private static string Render(string name, Format format, CoberturaReport report, int leftOut, string? active, int budget)
    {
        var files = report.Files.Select(f => (File: f.Key, Total: f.Value.Count, Covered: f.Value.Values.Count(h => h > 0),
                                              Uncovered: f.Value.Where(l => l.Value == 0).Select(l => l.Key).ToList()))
                                .ToList();
        int covered = files.Sum(f => f.Covered), total = files.Sum(f => f.Total);
        var sb = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"[{name}: a coverage report ({format switch { Format.Lcov => "lcov", Format.Jacoco => "JaCoCo", _ => "Cobertura" }}), {files.Count} source file(s), {covered} of {total} lines covered ({Percent(covered, total)}). "
          + $"Uncovered lines per file, the least covered first{(active is not null ? ", the file open in the editor before every other" : string.Empty)}."));
        if (leftOut > 0)
            sb.Append(CultureInfo.InvariantCulture, $" {leftOut} file(s) the report names are not this workspace's source (not found in it, outside it, tests, generated or third-party code): left out.");
        sb.Append(" Read it by lines (start_line) for the raw report.]\n");

        var partial = files.Where(f => f.Uncovered.Count > 0)
                           .OrderByDescending(f => f.File == active)
                           .ThenBy(f => f.Total == 0 ? 1.0 : (double)f.Covered / f.Total)
                           .ThenBy(f => f.File, StringComparer.Ordinal).ToList();
        if (active is not null && !report.Files.ContainsKey(active))
            sb.Append(CultureInfo.InvariantCulture, $"\nThe file open in the editor, {active}, is not in this report: no test run covered it, or the report is of another project.\n");
        else if (active is not null && partial.All(f => f.File != active))
            sb.Append(report.Files[active].Count == 0
                ? FormattableString.Invariant($"\nThe file open in the editor, {active}, has no line the report counts.\n")
                : FormattableString.Invariant($"\nThe file open in the editor, {active}, is fully covered ({report.Files[active].Count} lines).\n"));
        var said = 0;
        foreach (var f in partial)
        {
            var ranges = Ranges(f.Uncovered);
            var section = string.Create(CultureInfo.InvariantCulture, $"\n## {f.File} — {f.Covered} of {f.Total} lines covered ({Percent(f.Covered, f.Total)})\nnot covered: ")
                        + string.Join(", ", ranges.Take(MaxRanges))
                        + (ranges.Count > MaxRanges ? string.Create(CultureInfo.InvariantCulture, $", … and {ranges.Count - MaxRanges} more range(s)") : string.Empty)
                        + "\n";
            // Whole sections only: the first always, the next ones while they fit the page.
            if (said > 0 && sb.Length + section.Length > budget) break;
            sb.Append(section);
            said++;
        }
        if (said < partial.Count)
            sb.Append(CultureInfo.InvariantCulture, $"\n… {partial.Count - said} more file(s) with uncovered lines, not listed: the page is full.\n");
        var full = files.Count(f => f.Uncovered.Count == 0 && f.Total > 0);
        if (full > 0) sb.Append(CultureInfo.InvariantCulture, $"\n{full} file(s) fully covered: not listed.\n");
        return sb.ToString();
    }
}
