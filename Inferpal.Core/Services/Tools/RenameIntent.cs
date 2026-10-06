using System.IO;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Tools;

/// <summary>
/// The identifier rename a set of hand edits performs — every edit replaces ONE identifier by another and changes
/// nothing else — so that the edit tools can point at <c>rename_symbol</c>.
/// </summary>
/// <remarks>
/// <c>rename_symbol</c> finds every occurrence, callers and tests included, and writes them all or none. A rename made
/// by hand fails on one <c>old_content</c> that no longer matches — the whole batch is refused — or on one occurrence
/// left behind, which breaks the build under an answer that says "renamed everywhere". The edit tools therefore say
/// so: in their refusal, and after a write that leaves the old name elsewhere.
/// </remarks>
internal static class RenameIntent
{
    /// <summary>Identifier, whitespace run, or any other single character.</summary>
    private static readonly Regex Token = new(@"[\p{L}_][\p{L}\p{Nd}_]*|\s+|.",
        RegexOptions.CultureInvariant | RegexOptions.Singleline, RegexBudget.Default);

    /// <summary>Most source files the stale-name scan reads: past it the note says nothing rather than guess.</summary>
    internal const int MaxFilesScanned = 3000;

    /// <summary>
    /// (old, new) when every edit that changes something turns the same identifier into the same other identifier and
    /// changes nothing else; null otherwise (an edit that also changes code, two different renames, no change at all).
    /// </summary>
    public static (string Old, string New)? Of(IEnumerable<(string OldText, string NewText)> edits)
    {
        (string Old, string New)? pair = null;
        foreach (var (oldText, newText) in edits)
        {
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;
            var a = Tokens(oldText);
            var b = Tokens(newText);
            if (a is null || b is null || a.Count != b.Count) return null;
            for (var i = 0; i < a.Count; i++)
            {
                if (string.Equals(a[i], b[i], StringComparison.Ordinal)) continue;
                if (!IsIdentifier(a[i]) || !IsIdentifier(b[i])) return null;
                if (pair is null) pair = (a[i], b[i]);
                else if (pair.Value.Old != a[i] || pair.Value.New != b[i]) return null;
            }
        }
        return pair;
    }

    /// <summary>The sentence a refused hand rename ends with.</summary>
    public static string RefusalHint((string Old, string New) rename) =>
        $" These edits rename `{rename.Old}` to `{rename.New}`: {Call(rename)} finds every occurrence — callers and tests "
        + "included — and applies them in one call, instead of matching each old_content by hand.";

    /// <summary>
    /// The note after a hand rename that leaves <paramref name="rename"/>'s old name in other source files of the same
    /// language under <paramref name="root"/>; empty when none does, or when the scan cannot say.
    /// </summary>
    public static string StaleNote((string Old, string New) rename, string root, string editedFile)
    {
        var still = FilesStillNaming(root, Path.GetExtension(editedFile), rename.Old);
        if (still is null || still.Count == 0) return string.Empty;
        var named = string.Join(", ", still.Take(5).Select(p => Path.GetRelativePath(root, p)))
                  + (still.Count > 5 ? $" and {still.Count - 5} more" : string.Empty);
        return $"\n⚠ `{rename.Old}` still appears in {still.Count} file(s): {named}. If it is the same symbol, "
             + $"{Call(rename)} renames every remaining occurrence in one call.";
    }

    /// <summary>Source files of <paramref name="ext"/>'s language under <paramref name="root"/> that contain
    /// <paramref name="name"/> as a whole word; null when the scan cannot say (no root, too many files, unreadable).</summary>
    internal static List<string>? FilesStillNaming(string root, string ext, string name)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root) || string.IsNullOrEmpty(ext)) return null;
        var word = new Regex($@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(name)}(?![\p{{L}}\p{{Nd}}_])",
                             RegexOptions.CultureInvariant, RegexBudget.Default);
        var found = new List<string>();
        var scanned = 0;
        try
        {
            foreach (var file in WorkspaceScan.EnumerateSourceFamily(root, ext))
            {
                if (++scanned > MaxFilesScanned) return null;
                string text;
                try { text = TextFileEncoding.ReadText(file); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (word.IsMatch(text)) found.Add(file);
            }
        }
        catch (RegexMatchTimeoutException) { return null; }
        return found;
    }

    private static string Call((string Old, string New) rename) =>
        $"rename_symbol(old_name: \"{rename.Old}\", new_name: \"{rename.New}\")";

    private static List<string>? Tokens(string text)
    {
        try { return Token.Matches(text).Select(m => m.Value).ToList(); }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static bool IsIdentifier(string token) =>
        token.Length > 0 && (char.IsLetter(token[0]) || token[0] == '_');
}
