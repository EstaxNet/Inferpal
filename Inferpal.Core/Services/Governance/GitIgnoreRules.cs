using System.Text;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Governance;

/// <summary>
/// The rules of one ignore file in git's dialect (<c>gitignore(5)</c>): which of them decides for a path, if any.
/// </summary>
/// <remarks>
/// <para>
/// The dialect, as git reads it: blank lines and <c>#</c> comments are skipped (<c>\#</c> and <c>\!</c> are literal),
/// trailing spaces are dropped unless escaped, <c>!</c> negates, a <c>/</c> at the start or in the middle anchors the
/// pattern to the file's folder, a trailing <c>/</c> matches folders only; <c>*</c> and <c>?</c> never match a
/// <c>/</c>, <c>[…]</c> is a class (<c>!</c> or <c>^</c> negates it), a leading <c>**/</c> matches in every folder, a
/// trailing <c>/**</c> everything inside, a <c>/**/</c> zero or more folders. The LAST rule that matches decides.
/// </para>
/// <para>
/// ⚠ A path under an excluded folder is excluded, whatever a later <c>!</c> says: git does not look inside a folder it
/// ignores, so it cannot re-include a file there. Read without that rule, <c>build/</c> then <c>!build/keep.txt</c>
/// would let a file through that every other tool keeps out.
/// </para>
/// </remarks>
internal sealed class GitIgnoreRules
{
    /// <summary>One rule: where it is written, and what it says.</summary>
    internal sealed record Rule(string Source, int Line, string Pattern, bool Negated, bool FoldersOnly, Regex Matcher);

    /// <summary>A rule decided: the path is excluded (<see cref="Rule.Negated"/> false) or let through (true).</summary>
    internal readonly record struct Verdict(Rule Rule)
    {
        public bool Excluded => !Rule.Negated;
    }

    private readonly List<Rule> _rules;

    private GitIgnoreRules(List<Rule> rules) => _rules = rules;

    /// <summary>The rules read.</summary>
    internal IReadOnlyList<Rule> Rules => _rules;

    /// <summary>The longest pattern read; a longer line is dropped, and said once in <c>/diagnostics</c>.</summary>
    internal const int MaxPatternLength = 1024;

    /// <summary>The budget of one match: the patterns are the repository's, the paths the workspace's.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Reads the rules of <paramref name="content"/>, written in <paramref name="source"/>.</summary>
    /// <param name="ignoreCase">Whether names compare without their case — the file system's rule.</param>
    internal static GitIgnoreRules Parse(string source, string content, bool ignoreCase)
    {
        var rules = new List<Rule>();
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        var number = 0;
        foreach (var raw in content.Split('\n'))
        {
            number++;
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length > MaxPatternLength)
            {
                Diagnostics.RecordOnce("ContentExclusions",
                    $"{source} line {number} is longer than {MaxPatternLength} characters and was not read: its rule does not apply.",
                    $"{source}|{number}|long");
                continue;
            }
            if (line.Length == 0 || line[0] == '#') continue;
            line = TrimTrailingSpaces(line);
            if (line.Length == 0) continue;

            var negated = false;
            if (line[0] == '!')
            {
                negated = true;
                line = line[1..];
            }
            var foldersOnly = false;
            if (line.EndsWith('/') && !line.EndsWith("\\/", StringComparison.Ordinal))
            {
                foldersOnly = true;
                line = line.TrimEnd('/');
            }
            if (line.Length == 0) continue;

            // A slash at the start or in the middle anchors the pattern to the file's folder; none matches at any depth.
            var anchored = line.Contains('/');
            if (line[0] == '/') line = line[1..];
            if (line.Length == 0) continue;

            if (ToRegex(line, anchored) is not { } pattern) continue;   // an unclosed class: git ignores the line
            try
            {
                rules.Add(new Rule(source, number, raw.TrimEnd('\r'), negated, foldersOnly,
                                   new Regex(pattern, options, MatchTimeout)));
            }
            catch (ArgumentException) { /* not a pattern this dialect can say: the line is skipped, as git skips it */ }
        }
        return new GitIgnoreRules(rules);
    }

    /// <summary>
    /// The rule that decides for <paramref name="relativePath"/> (<c>/</c>-separated, relative to the file's folder) —
    /// the last that matches, or <c>null</c>. The path's folders are not looked at: <see cref="Judge"/> does that.
    /// </summary>
    internal Verdict? Last(string relativePath, bool isFolder)
    {
        for (var i = _rules.Count - 1; i >= 0; i--)
        {
            var rule = _rules[i];
            if (rule.FoldersOnly && !isFolder) continue;
            if (Matches(rule, relativePath)) return new Verdict(rule);
        }
        return null;
    }

    /// <summary>
    /// The rule that excludes <paramref name="relativePath"/>, its own or one of its folders' — <c>null</c> when it is not
    /// excluded. A folder excluded on the way excludes everything under it.
    /// </summary>
    internal Rule? Judge(string relativePath, bool isFolder)
    {
        var slash = relativePath.IndexOf('/');
        while (slash > 0)
        {
            if (Last(relativePath[..slash], isFolder: true) is { Excluded: true } folder) return folder.Rule;
            slash = relativePath.IndexOf('/', slash + 1);
        }
        return Last(relativePath, isFolder) is { Excluded: true } own ? own.Rule : null;
    }

    /// <remarks>⚠ A match that cannot be decided within the budget (retried once, a cold timeout under a loaded
    /// collector) keeps the file OUT of reach — an exclusion matches, a negation does not: the repository asked for the
    /// file to be kept away, and "unsure" must not read as "allowed". Said once in <c>/diagnostics</c>.</remarks>
    private static bool Matches(Rule rule, string path)
    {
        try { return RegexBudget.IsMatch(rule.Matcher, path); }
        catch (RegexMatchTimeoutException)
        {
            Diagnostics.RecordOnce("ContentExclusions",
                $"{rule.Source} line {rule.Line} (`{rule.Pattern}`) could not be matched in time: the file it was asked about "
                + "is kept out of reach.", $"{rule.Source}|{rule.Line}|timeout");
            return !rule.Negated;
        }
    }

    /// <summary>Trailing spaces go, unless the last one is escaped (<c>"a\ "</c> keeps one).</summary>
    private static string TrimTrailingSpaces(string line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ')
        {
            if (end >= 2 && line[end - 2] == '\\') break;
            end--;
        }
        return line[..end];
    }

    /// <summary>
    /// The pattern as a regex over the whole relative path; <c>null</c> for a class left open, which git ignores.
    /// </summary>
    internal static string? ToRegex(string pattern, bool anchored)
    {
        var sb = new StringBuilder(anchored ? "^" : "^(?:.*/)?");
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '*')
            {
                var stars = 1;
                while (i + stars < pattern.Length && pattern[i + stars] == '*') stars++;
                var atStart  = i == 0;
                var afterSep = i > 0 && pattern[i - 1] == '/';
                var end      = i + stars;
                var beforeSep = end < pattern.Length && pattern[end] == '/';
                var atEnd     = end == pattern.Length;
                if (stars >= 2 && (atStart || afterSep) && (beforeSep || atEnd))
                {
                    if (atEnd)
                    {
                        // `**` alone or `x/**`: everything (inside).
                        sb.Append(".*");
                        i = end;
                        continue;
                    }
                    // `**/` at the start or `/**/` in the middle: zero or more folders.
                    sb.Append("(?:.*/)?");
                    i = end + 1;
                    continue;
                }
                sb.Append("[^/]*");   // other runs of stars are one star
                i = end;
                continue;
            }
            if (c == '?')
            {
                sb.Append("[^/]");
                i++;
                continue;
            }
            if (c == '[')
            {
                if (Class(pattern, i, out var cls, out var next) is not true) return null;
                sb.Append(cls);
                i = next;
                continue;
            }
            if (c == '\\' && i + 1 < pattern.Length)
            {
                sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                i += 2;
                continue;
            }
            sb.Append(Regex.Escape(c.ToString()));
            i++;
        }
        // A folder that matches excludes what is under it (Judge looks at each folder); the pattern itself is whole.
        sb.Append('$');
        return sb.ToString();
    }

    /// <summary>A bracket expression from <paramref name="start"/>: as a regex class that never matches <c>/</c>.</summary>
    private static bool? Class(string pattern, int start, out string cls, out int next)
    {
        var sb = new StringBuilder("[");
        var i = start + 1;
        if (i < pattern.Length && pattern[i] is '!' or '^')
        {
            sb.Append('^');
            i++;
        }
        var first = true;
        var negated = sb.Length == 2;
        while (i < pattern.Length && (pattern[i] != ']' || first))
        {
            var c = pattern[i];
            if (c == '[' && i + 1 < pattern.Length && pattern[i + 1] == ':')
            {
                // A POSIX class: [:alpha:], [:digit:]…
                var close = pattern.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (close < 0) { cls = string.Empty; next = 0; return false; }
                sb.Append(PosixClass(pattern[(i + 2)..close]));
                i = close + 2;
                first = false;
                continue;
            }
            if (c == '\\' && i + 1 < pattern.Length)
            {
                // The next character, literally: `[\d]` is a `d` for git, never a regex digit class.
                var literal = pattern[i + 1];
                if (literal is '\\' or '^' or '[' or ']' or '-') sb.Append('\\');
                sb.Append(literal);
                i += 2;
                first = false;
                continue;
            }
            if (c is '\\' or '^' or '[' or ']') sb.Append('\\');
            sb.Append(c);
            i++;
            first = false;
        }
        if (i >= pattern.Length) { cls = string.Empty; next = 0; return false; }   // unclosed
        // ⚠ A class never matches the separator, negated or not: `a[!x]b` does not match `a/b`.
        if (negated) sb.Append('/');
        sb.Append(']');
        cls = negated ? sb.ToString() : "(?![/])" + sb;
        next = i + 1;
        return true;
    }

    private static string PosixClass(string name) => name switch
    {
        "alpha"  => "a-zA-Z",
        "digit"  => "0-9",
        "alnum"  => "a-zA-Z0-9",
        "upper"  => "A-Z",
        "lower"  => "a-z",
        "space"  => @"\s",
        "xdigit" => "0-9a-fA-F",
        "punct"  => @"!-\/:-@\[-`{-~",
        "blank"  => @" \t",
        _        => string.Empty,
    };
}
