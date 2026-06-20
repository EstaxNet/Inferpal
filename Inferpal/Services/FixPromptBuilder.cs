using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services;

/// <summary>
/// Pure formatting/parsing logic for the build-fix flow extracted from the tool-window
/// VM: the "fix these errors" prompt enriched with the affected files' contents, the
/// error-path extraction from compiler diagnostics, and the one-line banner preview.
/// File access is injected so the VM decides how (and whether) sources are read.
/// </summary>
internal static class FixPromptBuilder
{
    // dotnet build / VS error list: "/path/to/File.cs(12,5): error CS0001: ..."
    private static readonly Regex PathRx = new(
        @"^([^\r\n]+\.cs)\(\d+,\d+\):\s*(?:error|warning)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Max number of distinct affected files appended to the prompt.</summary>
    private const int MaxFiles = 5;

    /// <summary>Per-file content cap — keeps the prompt within a small model's budget.</summary>
    private const int MaxFileChars = 4000;

    /// <summary>
    /// The localized fix prompt followed by an "Affected files" section: up to
    /// <see cref="MaxFiles"/> files named in the diagnostics, each as a fenced block
    /// capped at <see cref="MaxFileChars"/> characters. <paramref name="tryReadFile"/>
    /// returns the file's content, or <c>null</c> to skip it (missing/unreadable).
    /// </summary>
    public static string Build(string rawErrors, Func<string, string?> tryReadFile)
    {
        var sb = new StringBuilder(Strings.PromptFixErrors(rawErrors));

        var headerWritten = false;
        foreach (var path in ExtractErrorPaths(rawErrors))
        {
            var content = tryReadFile(path);
            if (content is null) continue;
            if (content.Length > MaxFileChars)
                content = content[..MaxFileChars] + "\n…(truncated)";

            if (!headerWritten)
            {
                sb.AppendLine("\n\nAffected files:");
                headerWritten = true;
            }
            sb.AppendLine($"\n### {path}");
            sb.AppendLine("```");
            sb.AppendLine(content);
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Distinct .cs paths named in compiler diagnostics, first <see cref="MaxFiles"/>
    /// only, in order of first appearance.
    /// </summary>
    public static List<string> ExtractErrorPaths(string diagnosticOutput) =>
        PathRx.Matches(diagnosticOutput)
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxFiles)
            .ToList();

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
