using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

/// <summary>Context-provider categories offered when typing <c>@</c> in the chat prompt.</summary>
internal enum MentionKind { File, Code, Folder, Clipboard, Tree, Diff, Problems, Debugger }

/// <summary>
/// One @mention category. <see cref="Desc"/> is a factory so the popup text follows the
/// active UI language. Query-based categories (<c>@file</c>/<c>@code</c>/<c>@folder</c>)
/// commit to a sub-search; instant ones attach their context directly.
/// </summary>
internal sealed record MentionCategory(string Token, MentionKind Kind, Func<string> Desc, bool QueryBased);

/// <summary>Parsed state of the prompt's trailing @mention (see <see cref="MentionController.Parse"/>).</summary>
internal abstract record MentionState;

/// <summary>No trailing @mention — the popup should close.</summary>
internal sealed record MentionNone : MentionState
{
    public static readonly MentionNone Instance = new();
}

/// <summary>Still typing the category token (<c>@</c>, <c>@fi</c>, …) — show the category menu.</summary>
internal sealed record MentionTypingCategory(string Partial) : MentionState;

/// <summary>A query-based category is committed (<c>@file Foo</c>) — run the category's sub-search.</summary>
internal sealed record MentionCommittedQuery(string Category, string Query) : MentionState;

/// <summary>
/// Pure logic of the typed @mention providers: prompt parsing, category matching, prompt text
/// transforms, and the filesystem searches behind <c>@file</c>/<c>@folder</c>. Extracted from the
/// tool-window VM so it is unit-testable without VS — the VM keeps the debounce, the popup UI,
/// the attachments, and the tool-backed instant providers.
/// </summary>
internal static class MentionController
{
    /// <summary>Trailing bare token: <c>@</c> followed by word chars/dots at the end of the prompt.</summary>
    internal static readonly Regex MentionRegex =
        new(@"@([\w.]*)$", RegexOptions.Compiled);

    // A query-based category is "committed" once a space follows its @token
    // (e.g. "@file Foo", "@folder Serv", "@code auth logic"). Only file/code/folder drill down;
    // the instant providers (clipboard/tree/diff/problems) attach straight from the category menu.
    internal static readonly Regex MentionQueryRegex =
        new(@"@(?<cat>file|code|folder)[ ](?<q>[^\n@]*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static readonly MentionCategory[] Categories =
    [
        new("@file",      MentionKind.File,      () => Strings.MentionFileDesc,      QueryBased: true),
        new("@code",      MentionKind.Code,      () => Strings.MentionCodeDesc,      QueryBased: true),
        new("@folder",    MentionKind.Folder,    () => Strings.MentionFolderDesc,    QueryBased: true),
        new("@clipboard", MentionKind.Clipboard, () => Strings.MentionClipboardDesc, QueryBased: false),
        new("@tree",      MentionKind.Tree,      () => Strings.MentionTreeDesc,      QueryBased: false),
        new("@diff",      MentionKind.Diff,      () => Strings.MentionDiffDesc,      QueryBased: false),
        new("@problems",  MentionKind.Problems,  () => Strings.MentionProblemsDesc,  QueryBased: false),
        new("@debugger",  MentionKind.Debugger,  () => Strings.MentionDebuggerDesc,  QueryBased: false),
    ];

    /// <summary>File extensions eligible for <c>@file</c> search and folder context bodies.</summary>
    internal static readonly HashSet<string> IndexableExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".ts", ".js", ".tsx", ".jsx", ".py", ".go", ".java",
            ".cpp", ".h", ".hpp", ".razor", ".vue", ".fs",
            ".json", ".xml", ".yaml", ".yml", ".md", ".config",
            ".csproj", ".sln", ".props", ".targets",
        };

    private static readonly string[] SkippedDirs = ["bin", "obj", ".git", "node_modules", ".vs", "packages"];

    private static bool IsSkippedDir(string dir) => SkippedDirs.Contains(Path.GetFileName(dir));

    // ── Prompt parsing & transforms ───────────────────────────────────────────

    /// <summary>Classifies the prompt's trailing @mention (committed query &gt; typing &gt; none).</summary>
    public static MentionState Parse(string prompt)
    {
        var committed = MentionQueryRegex.Match(prompt);
        if (committed.Success)
            return new MentionCommittedQuery(
                committed.Groups["cat"].Value.ToLowerInvariant(),
                committed.Groups["q"].Value);

        var typing = MentionRegex.Match(prompt);
        return typing.Success
            ? new MentionTypingCategory(typing.Groups[1].Value)
            : MentionNone.Instance;
    }

    /// <summary>Categories whose token (without <c>@</c>) starts with the typed partial (lower-cased).</summary>
    public static IReadOnlyList<MentionCategory> MatchCategories(string partialLower) =>
        Categories.Where(c => c.Token[1..].StartsWith(partialLower, StringComparison.Ordinal)).ToList();

    /// <summary>Replaces the trailing partial token with the committed <c>@token␠</c> so the user types the query.</summary>
    public static string CommitCategory(string prompt, string token) =>
        MentionRegex.Replace(prompt, token + " ");

    /// <summary>Removes the trailing @mention token (committed <c>@file foo</c> or bare <c>@foo</c>).</summary>
    public static string StripMentionToken(string prompt)
    {
        var stripped = MentionQueryRegex.IsMatch(prompt)
            ? MentionQueryRegex.Replace(prompt, string.Empty)
            : MentionRegex.Replace(prompt, string.Empty);
        return stripped.TrimEnd();
    }

    /// <summary>Relative path label for the popup sub-line; falls back to the parent dir
    /// when <paramref name="rootDir"/> is not an ancestor of <paramref name="fullPath"/>.</summary>
    public static string RelLabel(string fullPath, string rootDir)
    {
        var rel = Path.GetRelativePath(rootDir, fullPath);
        return rel.StartsWith("..", StringComparison.Ordinal)
            ? (Path.GetDirectoryName(fullPath) ?? fullPath)
            : rel;
    }

    // ── Filesystem searches (@file / @folder) ─────────────────────────────────

    /// <summary>
    /// Fuzzy file search under <paramref name="rootDir"/>: name contains <paramref name="queryLower"/>,
    /// scored exact &gt; prefix &gt; contains, best 8 returned. Skips build/VCS folders.
    /// </summary>
    public static IReadOnlyList<string> FindFiles(string rootDir, string queryLower, CancellationToken ct)
    {
        var results = new List<(string Path, int Score)>();
        CollectFiles(rootDir, queryLower, results, 0, ct);
        return Rank(results);
    }

    /// <summary>Same as <see cref="FindFiles"/> for directories (empty query lists everything).</summary>
    public static IReadOnlyList<string> FindFolders(string rootDir, string queryLower, CancellationToken ct)
    {
        var results = new List<(string Path, int Score)>();
        CollectFolders(rootDir, queryLower, results, 0, ct);
        return Rank(results);
    }

    private static IReadOnlyList<string> Rank(List<(string Path, int Score)> results) =>
        results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => Path.GetFileName(r.Path), StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(r => r.Path)
            .ToList();

    private static void CollectFiles(
        string dir, string query, List<(string, int)> results, int depth, CancellationToken ct)
    {
        if (depth > 8 || ct.IsCancellationRequested || results.Count >= 100) return;
        if (IsSkippedDir(dir)) return;

        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                if (ct.IsCancellationRequested) return;
                if (!IndexableExtensions.Contains(Path.GetExtension(file))) continue;

                var name = Path.GetFileName(file).ToLowerInvariant();
                if (!name.Contains(query)) continue;

                var score = name.StartsWith(query, StringComparison.Ordinal) ? 2 : 1;
                if (name == query || name == query + Path.GetExtension(name)) score = 3;
                results.Add((file, score));
            }

            foreach (var sub in Directory.GetDirectories(dir))
                CollectFiles(sub, query, results, depth + 1, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("MentionController.CollectFiles", ex); }
    }

    private static void CollectFolders(
        string dir, string query, List<(string, int)> results, int depth, CancellationToken ct)
    {
        if (depth > 6 || ct.IsCancellationRequested || results.Count >= 60) return;

        try
        {
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                if (ct.IsCancellationRequested) return;
                if (IsSkippedDir(subDir)) continue;

                var nl = Path.GetFileName(subDir).ToLowerInvariant();
                if (query.Length == 0 || nl.Contains(query))
                {
                    var score = nl.StartsWith(query, StringComparison.Ordinal) ? 2 : 1;
                    if (nl == query) score = 3;
                    results.Add((subDir, score));
                }
                CollectFolders(subDir, query, results, depth + 1, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("MentionController.CollectFolders", ex); }
    }

    // ── @folder context body ──────────────────────────────────────────────────

    /// <summary>Concatenates the text files under a folder (tree header + bodies) within a size budget.</summary>
    public static string BuildFolderContext(string folderPath, CancellationToken ct)
    {
        const int MaxFiles = 30;
        const int MaxTotalChars = 60_000;

        var sb = new StringBuilder();
        sb.Append("Folder: ").AppendLine(folderPath).AppendLine();

        var files = new List<string>();
        CollectFolderFiles(folderPath, files, 0, ct);

        sb.AppendLine("Files:");
        foreach (var f in files)
            sb.Append("  ").AppendLine(Path.GetRelativePath(folderPath, f));
        sb.AppendLine();

        foreach (var f in files.Take(MaxFiles))
        {
            if (ct.IsCancellationRequested) break;
            string body;
            try { body = File.ReadAllText(f); } catch { continue; }

            var header = $"\n----- {Path.GetRelativePath(folderPath, f)} -----\n";
            if (sb.Length + header.Length + body.Length > MaxTotalChars)
            {
                var budget = MaxTotalChars - sb.Length - header.Length;
                if (budget < 200) break;
                body = body[..Math.Min(body.Length, budget)] + "\n…(truncated)";
            }
            sb.Append(header).Append(body);
        }
        return sb.ToString();
    }

    private static void CollectFolderFiles(string dir, List<string> results, int depth, CancellationToken ct)
    {
        if (depth > 4 || ct.IsCancellationRequested || results.Count >= 200) return;
        if (IsSkippedDir(dir)) return;

        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                if (ct.IsCancellationRequested) return;
                if (IndexableExtensions.Contains(Path.GetExtension(file))) results.Add(file);
            }
            foreach (var subDir in Directory.GetDirectories(dir))
                CollectFolderFiles(subDir, results, depth + 1, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("MentionController.CollectFolderFiles", ex); }
    }
}
