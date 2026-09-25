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
    /// <paramref name="tryReadFile"/> returns the file's content, or <c>null</c> to skip it
    /// (missing/unreadable).
    /// </summary>
    public static string Build(string rawErrors, Func<string, string?> tryReadFile)
    {
        var sb = new StringBuilder(Strings.PromptFixErrors(rawErrors));

        var headerWritten = false;
        foreach (var (path, lines) in ExtractErrorSites(rawErrors))
        {
            var content = tryReadFile(path);
            if (content is null) continue;

            if (!headerWritten)
            {
                sb.AppendLine("\n\nAffected files:");
                headerWritten = true;
            }
            sb.AppendLine($"\n### {path}");
            if (content.Length <= MaxFileChars)
            {
                sb.AppendLine("```");
                sb.AppendLine(content);
                sb.AppendLine("```");
            }
            else
            {
                AppendWindows(sb, content, lines);
            }
        }

        return sb.ToString();
    }

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
        ExtractErrorSites(diagnosticOutput).Select(s => s.Path).ToList();

    /// <summary>
    /// <see cref="ExtractErrorPaths"/> with, for each path, the lines its diagnostics point at.
    /// </summary>
    private static List<(string Path, List<int> Lines)> ExtractErrorSites(string diagnosticOutput)
    {
        var sites = new List<(string Path, List<int> Lines)>();
        foreach (Match m in SiteRx.Matches(diagnosticOutput))
        {
            var path = m.Groups[1].Value.Trim();
            var line = int.TryParse(m.Groups[2].ValueSpan, out var n) ? n : 0;
            var at   = sites.FindIndex(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) { sites[at].Lines.Add(line); continue; }
            if (sites.Count == MaxFiles) continue;
            sites.Add((path, [line]));
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
