using System.Text.RegularExpressions;

namespace Inferpal.Services;

/// <summary>
/// The C# type declarations a source spells, read by pattern: for the readers that scan many files and do not build a
/// syntax tree for each — the project map, the context of <c>/doc</c>, the impact analysis and the pattern tier of the
/// chunker.
/// </summary>
/// <remarks>
/// ⚠ One reader for the four. Each kept its own pattern, each with its own hole: two required a <c>{</c> right after the
/// name, the generics or the base list, and so missed every positional record (<c>record Point(int X, int Y);</c>) and
/// every class with a primary constructor — 258 of the 259 records of this repository's Core were absent from its map;
/// two read <c>record struct Point</c> as a type named "struct", a name the impact analysis then found in every file
/// that says "struct".
/// </remarks>
internal static class CSharpTypeDeclarations
{
    /// <summary>One declaration.</summary>
    /// <param name="Kind">The keyword: <c>class</c>, <c>interface</c>, <c>struct</c>, <c>enum</c> or <c>record</c> (a
    /// record struct included).</param>
    /// <param name="Modifiers">As written: <c>public</c>, <c>sealed</c>, <c>abstract</c>…</param>
    /// <param name="BaseTypes">Simple names of the base list, without type arguments or constructor arguments.</param>
    /// <param name="Index">Where the declaration's line starts in the source.</param>
    internal sealed record Declaration(
        string Kind, string Name, IReadOnlyList<string> Modifiers, IReadOnlyList<string> BaseTypes, int Index);

    // Attributes on the same line, then modifiers, then the keyword and the name: what starts a declaration on its line.
    private const string Head =
        @"^[ \t]*(?:\[[^\]\r\n]*\][ \t]*)*" +
        @"(?<mods>(?:(?:public|internal|private|protected|file|sealed|abstract|static|partial|readonly|new|unsafe|ref)\s+)*)" +
        @"(?<kind>class|interface|record|enum|struct)\s+(?:(?:class|struct)\s+)?(?<name>[\p{L}\p{Nl}_]\w*)";

    /// <summary>A line that starts a declaration (group <c>name</c>), for readers that walk a file line by line.</summary>
    internal static readonly Regex LineStart = new(Head, RegexOptions.Compiled, RegexBudget.Default);

    private static readonly Regex Starts = new(Head, RegexOptions.Compiled | RegexOptions.Multiline, RegexBudget.Default);

    /// <summary>How far past a name the reader looks for its type parameters, primary constructor and base list.</summary>
    private const int MaxScan = 4_000;

    /// <summary>Every declaration of <paramref name="source"/>, in order.</summary>
    internal static IReadOnlyList<Declaration> Read(string source)
    {
        var found = new List<Declaration>();
        foreach (Match m in Starts.Matches(source))
        {
            var i = SkipBalanced(source, SkipSpace(source, m.Index + m.Length), '<', '>');
            i = SkipBalanced(source, SkipSpace(source, i), '(', ')');
            i = SkipSpace(source, i);
            var bases = i < source.Length && source[i] == ':' ? BaseList(source, i + 1) : string.Empty;
            found.Add(new Declaration(
                m.Groups["kind"].Value, m.Groups["name"].Value,
                m.Groups["mods"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
                BaseTypes(bases), m.Index));
        }
        return found;
    }

    /// <summary>Simple names of a base list: <c>Base(log), IDictionary&lt;string, int&gt;</c> → <c>Base</c>,
    /// <c>IDictionary</c>. Commas inside type or constructor arguments do not split.</summary>
    internal static IReadOnlyList<string> BaseTypes(string list)
    {
        var names = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= list.Length; i++)
        {
            var c = i < list.Length ? list[i] : ',';
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                var part = list[start..i];
                var cut = part.IndexOfAny(['<', '(']);
                var name = (cut >= 0 ? part[..cut] : part).Trim();
                if (name.Length > 0) names.Add(name);
                start = i + 1;
            }
        }
        return names;
    }

    private static int SkipSpace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    /// <summary>Past the balanced group that opens at <paramref name="i"/>, or <paramref name="i"/> when none does (or
    /// it does not close within reach).</summary>
    private static int SkipBalanced(string s, int i, char open, char close)
    {
        if (i >= s.Length || s[i] != open) return i;
        var depth = 0;
        for (var j = i; j < s.Length && j - i < MaxScan; j++)
        {
            if (s[j] == open) depth++;
            else if (s[j] == close && --depth == 0) return j + 1;
        }
        return i;
    }

    /// <summary>The base list that starts at <paramref name="i"/>: up to the body, the <c>;</c> of a positional record,
    /// or the constraints (<c>where</c>), outside any brackets.</summary>
    private static string BaseList(string s, int i)
    {
        var depth = 0;
        for (var j = i; j < s.Length && j - i < MaxScan; j++)
        {
            var c = s[j];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && (c is '{' or ';' || IsWordAt(s, j, "where"))) return s[i..j];
        }
        return string.Empty;
    }

    private static bool IsWordAt(string s, int i, string word) =>
        string.CompareOrdinal(s, i, word, 0, word.Length) == 0
        && (i == 0 || !IsIdentifierChar(s[i - 1]))
        && (i + word.Length >= s.Length || !IsIdentifierChar(s[i + word.Length]));

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
