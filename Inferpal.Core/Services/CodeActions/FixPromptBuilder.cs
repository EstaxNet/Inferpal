using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.CodeActions;

/// <summary>
/// Pure formatting/parsing logic for the build-fix flow extracted from the tool-window
/// VM: the "fix these errors" prompt enriched with the affected files' contents, the
/// error-path extraction from compiler diagnostics, and the one-line banner preview.
/// The disk reader lives here (<see cref="ReadFromDisk"/>), where a test can run it; file access stays
/// injectable so the formatting can be tested without files.
/// </summary>
internal static class FixPromptBuilder
{
    // dotnet build / VS error list: "/path/to/File.cs(12,5): error CS0001: ..." — the path and the line.
    private static readonly Regex SiteRx = new(
        @"^([^\r\n]+\.cs)\((\d+),\d+\):\s*(?:error|warning)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled, RegexBudget.Default);

    /// <summary>Max number of distinct affected files appended to the prompt.</summary>
    private const int MaxFiles = 5;

    /// <summary>Per-file content cap — keeps the prompt within a small model's budget.</summary>
    private const int MaxFileChars = 4000;

    /// <summary>Problems listed from an editor's selection; the rest is counted above them.</summary>
    internal const int MaxProblems = 100;

    /// <summary>Characters the listed problems take at most. With the files' own caps, the attachment stays under what an
    /// attachment sends (<see cref="Agent.AttachmentCap.MaxChars"/>), so neither editor cuts it again.</summary>
    internal const int MaxProblemChars = 30_000;

    /// <summary>Lines kept from the top of a file that does not fit: its usings and namespace.</summary>
    private const int HeadLines = 12;

    /// <summary>Lines shown before and after each diagnostic line, in a file that does not fit.</summary>
    private const int ContextLines = 10;

    /// <summary>The fix prompt, with the affected files read from disk by <see cref="ReadFromDisk"/>.</summary>
    public static string Build(string rawErrors) => Build(rawErrors, ReadFromDisk);

    /// <summary>
    /// A diagnosed file as the model must see it. ⚠ Decoded like the file an edit rewrites
    /// (<see cref="Tools.TextFileEncoding"/>): the lines around each error are exactly what the model copies into
    /// <c>old_content</c>, and a Windows-1252 file read as UTF-8 shows every accent as "�" — an edit quoting such a
    /// line then matches nothing in the file the tool decodes. <c>null</c> skips the file.
    /// </summary>
    internal static string? ReadFromDisk(string path)
    {
        if (!File.Exists(path)) return null;
        try { return Tools.TextFileEncoding.ReadText(path); }
        catch (Exception ex)
        {
            Diagnostics.Swallow("FixPromptBuilder.ReadFromDisk", ex);
            return null;
        }
    }

    /// <summary>
    /// The localized fix prompt followed by an "Affected files" section: up to
    /// <see cref="MaxFiles"/> files named in the diagnostics, each whole when it fits in
    /// <see cref="MaxFileChars"/>, otherwise as the windows <see cref="AppendWindows"/> picks.
    /// <paramref name="tryReadFile"/> returns the file's content, or <c>null</c> when it cannot be read.
    /// </summary>
    /// <remarks>
    /// ⚠ The section reads as "the files in error": a file past the cap, or one that could not be read, is NAMED above
    /// the files shown — left out in silence, the five shown pass for the whole build, and a fix of those five for a
    /// finished one.
    /// </remarks>
    public static string Build(string rawErrors, Func<string, string?> tryReadFile)
    {
        var sb = new StringBuilder(Strings.PromptFixErrors(rawErrors));
        AppendAffectedFiles(sb, ExtractErrorSites(rawErrors), tryReadFile);
        return sb.ToString();
    }

    /// <summary>
    /// The attachment of a "fix these problems" question: each problem as a build prints it, then the "Affected files"
    /// section of <see cref="Build(string, Func{string, string?})"/> — the lines around each problem, the same caps, the
    /// same files left out and named.
    /// </summary>
    /// <remarks>The sites come from the entries, never parsed back out of the lines: a problem in a <c>.ts</c>, a
    /// <c>.xaml</c> or a project file keeps its code, where the build-output parser reads C# files only.</remarks>
    internal static string ForProblems(IReadOnlyList<ProblemEntry> problems, Func<string, string?> tryReadFile)
    {
        // ⚠ Errors first, then the caps: a whole list selected (Ctrl+A) is hundreds of warnings with the errors among them.
        var ranked = problems.OrderBy(p => p.Severity switch { "error" => 0, "warning" => 1, _ => 2 }).ToList();
        var lines  = new List<string>();
        var used   = 0;
        foreach (var p in ranked)
        {
            if (lines.Count == MaxProblems) break;
            var line = CompilerLine(p);
            if (used + line.Length > MaxProblemChars)
            {
                if (lines.Count > 0) break;
                line = SafeTruncate.Truncate(line, MaxProblemChars) + $"…(cut at {MaxProblemChars} of {line.Length} characters)";
            }
            lines.Add(line);
            used += line.Length + 1;
        }

        var sb = new StringBuilder();
        if (lines.Count < ranked.Count)
            sb.AppendLine($"({ranked.Count - lines.Count} more problem(s) selected, not listed: the first {lines.Count}, "
                        + "errors first, are)");
        var sites = new List<(string Path, List<int> Lines)>();
        foreach (var (p, line) in ranked.Zip(lines))
        {
            sb.AppendLine(line);
            if (!string.IsNullOrEmpty(p.File)) AddSite(sites, p.File, p.Line);
        }
        AppendAffectedFiles(sb, sites, tryReadFile);
        return sb.ToString().TrimEnd();
    }

    private static void AddSite(List<(string Path, List<int> Lines)> sites, string path, int line)
    {
        var at = sites.FindIndex(s => string.Equals(s.Path, path, PathComparer.Comparison));
        if (at >= 0) sites[at].Lines.Add(line);
        else sites.Add((path, [line]));
    }

    /// <summary><c>File.cs(12,5): error CS0103: text</c> — a problem written as a build prints it.</summary>
    internal static string CompilerLine(ProblemEntry p)
    {
        var where = string.IsNullOrEmpty(p.File) ? string.Empty
                  : p.Line > 0 ? $"{p.File}({p.Line}{(p.Column > 0 ? $",{p.Column}" : string.Empty)}): "
                  : $"{p.File} : ";
        var code   = string.IsNullOrEmpty(p.Code) ? string.Empty : $" {p.Code}";
        var source = string.IsNullOrEmpty(p.Source) ? string.Empty : $" ({p.Source})";
        return $"{where}{p.Severity}{code}: {p.Text}{source}";
    }

    /// <summary>The "Affected files" section: each file whole when it fits, otherwise its windows — what is left out,
    /// named above what is shown.</summary>
    private static void AppendAffectedFiles(StringBuilder sb, List<(string Path, List<int> Lines)> sites,
                                            Func<string, string?> tryReadFile)
    {
        var files    = new StringBuilder();
        var unread   = new List<string>();
        var excluded = new Governance.ContentExclusions.Tally();
        foreach (var (path, lines) in sites.Take(MaxFiles))
        {
            // Excluded by the workspace's ignore files: never read, and said with the others left out.
            if (Governance.ContentExclusions.Of(path) is { } why) { excluded.Add(path, why); continue; }
            var content = tryReadFile(path);
            if (content is null) { unread.Add(path); continue; }

            files.AppendLine($"\n### {path}");
            if (content.Length <= MaxFileChars)
            {
                files.AppendLine("```");
                files.AppendLine(content);
                files.AppendLine("```");
            }
            else
            {
                AppendWindows(files, content, lines);
            }
        }
        var notShown = sites.Skip(MaxFiles).Select(s => s.Path).ToList();
        if (files.Length == 0 && unread.Count == 0 && notShown.Count == 0 && excluded.Count == 0) return;

        sb.AppendLine("\n\nAffected files:");
        if (excluded.Count > 0)
            sb.AppendLine($"({excluded.Count} file(s) excluded from AI tools by {excluded.Rules()}, not shown — they exist, "
                        + "and their content is not read)");
        if (notShown.Count > 0)
            sb.AppendLine($"({notShown.Count} more file(s) with diagnostics past the first {MaxFiles}, not shown here: "
                        + $"{Named(notShown)} — read_file gives them)");
        if (unread.Count > 0)
            sb.AppendLine($"(could not be read, not shown: {Named(unread)})");
        sb.Append(files);
    }

    /// <summary>Paths for a note: the first ten, and how many more — a note does not become the list it qualifies.</summary>
    private static string Named(List<string> paths) =>
        paths.Count <= 10 ? string.Join(", ", paths) : $"{string.Join(", ", paths.Take(10))} and {paths.Count - 10} more";

    /// <summary>
    /// A file that does not fit: its head, then the lines around each diagnostic, each window in its own
    /// block under the lines it spans, within <see cref="MaxFileChars"/> — and what is left out, said.
    /// </summary>
    /// <remarks>
    /// ⚠ The first <see cref="MaxFileChars"/> characters are not "the file": half the C# files of an
    /// ordinary code base are longer, and most of their lines sit past that cut — so for one diagnostic
    /// in two the line in error was not in what the model read, under a bare "…(truncated)" that did not
    /// say so, and the chat's Fix button can send this prompt with no tool to read further. The windows
    /// follow the diagnostics; the head keeps the usings a missing-type error is fixed in. The line ranges
    /// sit OUTSIDE the code blocks, so a model that copies a block into an edit copies code only.
    /// </remarks>
    private static void AppendWindows(StringBuilder sb, string content, IReadOnlyList<int> errorLines)
    {
        var lines = content.Split('\n');
        var total = content.EndsWith('\n') ? lines.Length - 1 : lines.Length;

        var wanted = new List<(int From, int To)> { (1, Math.Min(HeadLines, total)) };
        wanted.AddRange(errorLines.Where(l => l >= 1 && l <= total).Distinct().Order()
                                  .Select(l => (Math.Max(1, l - ContextLines), Math.Min(total, l + ContextLines))));

        var windows = new List<(int From, int To)>();
        foreach (var w in wanted.OrderBy(w => w.From))
        {
            if (windows.Count > 0 && w.From <= windows[^1].To + 1)
                windows[^1] = (windows[^1].From, Math.Max(windows[^1].To, w.To));
            else
                windows.Add(w);
        }

        sb.AppendLine($"({total} lines — shown: the top of the file and the lines around each diagnostic; "
                    + "read_file gives any other range)");

        var used = 0;
        var left = new List<int>();
        foreach (var (from, to) in windows)
        {
            var body = string.Join('\n', lines[(from - 1)..to].Select(l => l.TrimEnd('\r')));
            if (used > 0 && used + body.Length > MaxFileChars)
            {
                left.AddRange(errorLines.Where(l => l >= from && l <= to));
                continue;
            }
            if (body.Length > MaxFileChars - used)   // one line longer than the whole budget (minified code)
                body = SafeTruncate.Truncate(body, MaxFileChars - used)
                     + $"\n…(cut at {MaxFileChars - used} of {body.Length} characters)";
            used += body.Length;

            sb.AppendLine(from == to ? $"Line {from} of {total}:" : $"Lines {from}–{to} of {total}:");
            sb.AppendLine("```");
            sb.AppendLine(body);
            sb.AppendLine("```");
        }

        if (left.Count > 0)
            sb.AppendLine($"(diagnostic line(s) {string.Join(", ", left.Distinct().Order())} not shown: "
                        + $"past the {MaxFileChars}-character budget — read_file gives them)");
    }

    /// <summary>
    /// Distinct .cs paths named in compiler diagnostics, first <see cref="MaxFiles"/>
    /// only, in order of first appearance.
    /// </summary>
    public static List<string> ExtractErrorPaths(string diagnosticOutput) =>
        ExtractErrorSites(diagnosticOutput).Select(s => s.Path).Take(MaxFiles).ToList();

    /// <summary>
    /// Every distinct path named, in order of first appearance, with the lines its diagnostics point at — the cap
    /// is <see cref="Build(string)"/>'s, which names what it leaves out.
    /// </summary>
    private static List<(string Path, List<int> Lines)> ExtractErrorSites(string diagnosticOutput)
    {
        var sites = new List<(string Path, List<int> Lines)>();
        foreach (Match m in SiteRx.Matches(diagnosticOutput))
        {
            AddSite(sites, m.Groups[1].Value.Trim(), int.TryParse(m.Groups[2].ValueSpan, out var n) ? n : 0);
        }
        return sites;
    }

    /// <summary>
    /// The first non-blank error line, trimmed and capped at <paramref name="maxLength"/>
    /// characters (ellipsis included) — display text for the "Build Failed" banner.
    /// </summary>
    public static string FirstErrorLine(string errorLines, int maxLength = 120)
    {
        var first = errorLines
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return first.Length > maxLength ? first[..(maxLength - 3)] + "…" : first;
    }
}
