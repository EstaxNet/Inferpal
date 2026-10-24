using System.Text.RegularExpressions;

namespace Inferpal.Services.Coverage;

/// <summary>A method no test fully reaches: its file (relative), its name, its lines, and those of them no test runs.</summary>
internal sealed record CoverageTarget(string File, string Name, int First, int Last, IReadOnlyList<int> Uncovered);

/// <summary>
/// The targets of <c>/test coverage</c>, chosen without the model: the methods of the source files with at least
/// <see cref="MinUncovered"/> lines no test runs, the most uncovered first.
/// </summary>
internal static class CoverageTargets
{
    /// <summary>Targets one run takes; the rest are said, for another run.</summary>
    internal const int MaxPerRun = 10;

    /// <summary>A method with fewer uncovered lines is not worth a test of its own.</summary>
    internal const int MinUncovered = 2;

    private static readonly Regex PythonDef = new(@"^(?<indent>[ \t]*)(async[ \t]+)?def[ \t]+(?<name>\w+)",
                                                  RegexOptions.CultureInvariant, RegexBudget.Default);

    private static readonly Regex PythonClass = new(@"^(?<indent>[ \t]*)class[ \t]+(?<name>\w+)",
                                                    RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <param name="folder">The folder asked for, relative to the root with forward slashes; empty for the whole workspace.</param>
    /// <param name="readLines">The lines of a file (relative path); empty when it cannot be read.</param>
    public static List<CoverageTarget> Rank(CoberturaReport report, string folder, Func<string, string[]> readLines)
    {
        var found = new List<CoverageTarget>();
        foreach (var (file, lines) in report.Files)
        {
            if (!InFolder(file, folder)) continue;
            foreach (var (name, first, last) in Spans(report, file, readLines))
            {
                var uncovered = Enumerable.Range(first, last - first + 1)
                                          .Where(n => lines.TryGetValue(n, out var hits) && hits == 0).ToList();
                if (uncovered.Count >= MinUncovered) found.Add(new CoverageTarget(file, name, first, last, uncovered));
            }
        }
        return [.. found.OrderByDescending(t => t.Uncovered.Count)
                        .ThenBy(t => t.File, StringComparer.Ordinal).ThenBy(t => t.First)];
    }

    /// <summary>Whether <paramref name="file"/> is <paramref name="folder"/> or under it.</summary>
    internal static bool InFolder(string file, string folder) =>
        folder.Length == 0
        || file.Equals(folder, PathComparer.Comparison)
        || file.StartsWith(folder.TrimEnd('/') + "/", PathComparer.Comparison);

    /// <summary>The methods of <paramref name="file"/>: as the tool names them, or, for Python (coverage.py names none),
    /// each outermost function by its indentation.</summary>
    private static IEnumerable<(string Name, int First, int Last)> Spans(CoberturaReport report, string file,
                                                                        Func<string, string[]> readLines)
    {
        if (report.Methods.TryGetValue(file, out var named) && named.Count > 0)
        {
            // istanbul names a method by its declaration line only: it runs to the line before the next one. ⚠ Only for a
            // file whose every method is one line: coverlet gives each its lines, and a one-line member there is one line.
            var ordered = named.OrderBy(m => m.First).ToList();
            var lastLine = report.Files[file].Keys.DefaultIfEmpty(0).Max();
            var declarationsOnly = ordered.All(m => m.First == m.Last);
            for (var i = 0; i < ordered.Count; i++)
            {
                var (name, first, last) = ordered[i];
                if (declarationsOnly)
                    last = i + 1 < ordered.Count ? Math.Max(first, ordered[i + 1].First - 1) : Math.Max(first, lastLine);
                yield return (name.Length > 0 ? name : $"line {first}", first, last);
            }
            yield break;
        }
        if (!file.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) yield break;
        foreach (var span in PythonFunctions(readLines(file))) yield return span;
    }

    /// <summary>The outermost functions and methods of a Python file: a <c>def</c> runs until a line indented no deeper
    /// than it; a function nested in another is part of it. A method is named with its class (<c>Row.__repr__</c>): two
    /// classes of one file often share method names.</summary>
    internal static IEnumerable<(string Name, int First, int Last)> PythonFunctions(string[] lines)
    {
        string? name = null;
        int first = 0, indent = 0, last = 0, brackets = 0;
        bool signature = false, inString = false;
        var classes = new List<(int Indent, string Name)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            var depth   = line.Length - line.TrimStart().Length;
            var comment = !inString && line.TrimStart().StartsWith('#');
            // ⚠ Neither the end of a signature split over lines (black writes its ") -> int:" at the def's indentation) nor
            // a docstring's text written flush left ends the function.
            if (name is not null && depth <= indent && !comment && !signature && !inString)
            {
                yield return (name, first, last);
                name = null;
            }
            if (name is null && !comment)
            {
                classes.RemoveAll(c => depth <= c.Indent);
                if (RegexBudget.IsMatch(PythonClass, line) && PythonClass.Match(line) is { Success: true } cls)
                    classes.Add((cls.Groups["indent"].Value.Length, cls.Groups["name"].Value));
            }
            if (name is null && RegexBudget.RetryOnce(() => PythonDef.IsMatch(line)) && PythonDef.Match(line) is { Success: true } def)
            {
                var qualified = string.Join('.', classes.Select(c => c.Name).Append(def.Groups["name"].Value));
                (name, first, indent) = (qualified, i + 1, def.Groups["indent"].Value.Length);
                (signature, brackets, inString) = (true, 0, false);
            }
            if (name is null) continue;
            last = i + 1;
            if (signature)
            {
                // The signature ends on the line that closes its brackets (a one-line def ends on its own line).
                var code = line.Contains('#') ? line[..line.IndexOf('#')] : line;
                brackets += code.Count(c => c is '(' or '[' or '{') - code.Count(c => c is ')' or ']' or '}');
                if (brackets <= 0) signature = false;
            }
            else if ((Occurrences(line, "\"\"\"") + Occurrences(line, "'''")) % 2 == 1)
            {
                inString = !inString;
            }
        }
        if (name is not null) yield return (name, first, last);
    }

    private static int Occurrences(string text, string token)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(token, at, StringComparison.Ordinal)) >= 0) { count++; at += token.Length; }
        return count;
    }
}
