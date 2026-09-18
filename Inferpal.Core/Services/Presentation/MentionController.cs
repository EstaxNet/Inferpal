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
        new(@"@([\w.]*)$", RegexOptions.Compiled, RegexBudget.Default);

    // A query-based category is "committed" once a space follows its @token
    // (e.g. "@file Foo", "@folder Serv", "@code auth logic"). Only file/code/folder drill down;
    // the instant providers (clipboard/tree/diff/problems) attach straight from the category menu.
    internal static readonly Regex MentionQueryRegex =
        new(@"@(?<cat>file|code|folder)[ ](?<q>[^\n@]*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexBudget.Default);

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
            ".csproj", ".sln", ".slnx", ".props", ".targets",
        };

    private static bool IsSkippedDir(string dir) => WorkspaceScan.IsExcludedDirName(dir);

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

    /// <summary>Files whose text is included; past it the listing stands alone.</summary>
    private const int MaxFiles = 30;

    /// <summary>Characters the bodies may take together.</summary>
    private const int MaxTotalChars = 60_000;

    /// <summary>Files the walk lists at most.</summary>
    private const int MaxWalkFiles = 200;

    /// <summary>Levels below the folder the walk descends into.</summary>
    private const int MaxWalkDepth = 4;

    /// <summary>
    /// Concatenates the text files under a folder (tree header + bodies) within a size budget.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Every cut is stated, because the listing and the bodies are not the same set.</b> The
    /// "Files:" block names every file the walk found; the text below it stops at
    /// <see cref="MaxFiles"/> and at a character budget, a file may have been unreadable, and the
    /// walk itself has a file ceiling and a depth ceiling. A model handed 120 names and 30 bodies
    /// cannot tell which is which — it answers "that symbol is not used in this folder" about a
    /// folder it was shown the index of. The per-body "…(truncated)" marker was already here, so the
    /// rule was known; it was applied to the one cut that is visible in the text, and to none of the
    /// four that are not.
    /// </remarks>
    public static string BuildFolderContext(string folderPath, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("Folder: ").AppendLine(folderPath).AppendLine();

        var files  = new List<string>();
        var limits = new FolderWalkLimits();
        CollectFolderFiles(folderPath, files, 0, ct, limits);

        sb.AppendLine("Files:");
        foreach (var f in files)
            sb.Append("  ").AppendLine(Path.GetRelativePath(folderPath, f));
        sb.AppendLine();

        var attempted  = Math.Min(files.Count, MaxFiles);
        var included   = 0;
        var unreadable = 0;
        var budgetStop = false;

        foreach (var f in files.Take(MaxFiles))
        {
            if (ct.IsCancellationRequested) break;
            string body;
            // Counted, not traced: one ring entry per unreadable file, on a folder the user may
            // attach repeatedly, is the noise RecordOnce exists to prevent. The count below is the
            // channel, and it goes where the reader of this context will see it.
            try { body = File.ReadAllText(f); } catch { unreadable++; continue; }

            var header = $"\n----- {Path.GetRelativePath(folderPath, f)} -----\n";
            if (sb.Length + header.Length + body.Length > MaxTotalChars)
            {
                var budget = MaxTotalChars - sb.Length - header.Length;
                if (budget < 200) { budgetStop = true; break; }
                body = body[..Math.Min(body.Length, budget)] + "\n…(truncated)";
            }
            sb.Append(header).Append(body);
            included++;
        }

        // One sentence per cause, and only when that cause fired. They are not interchangeable: a
        // body cap is narrowed by attaching a subfolder, a character budget by attaching fewer
        // files, an unreadable file by a permission, a walk ceiling by nothing the user can do
        // without splitting the folder.
        var notes = new List<string>(4);
        if (files.Count > MaxFiles)
            notes.Add($"(bodies: the first {MaxFiles} of {files.Count} listed files — the rest are named above but NOT included)");
        if (budgetStop)
            notes.Add($"(stopped at the {MaxTotalChars:N0}-character budget: {attempted - included - unreadable} more listed file(s) not included)");
        if (unreadable > 0)
            notes.Add($"({unreadable} file(s) could not be read and are not included)");
        if (limits.FileCapHit)
            notes.Add($"(the folder walk stopped at {MaxWalkFiles} files — this folder holds more, and they are not listed above)");
        if (limits.DepthCapHit)
            notes.Add($"(subfolders deeper than {MaxWalkDepth} levels were not listed)");

        if (notes.Count > 0) sb.Append('\n').AppendLine().AppendJoin('\n', notes);
        return sb.ToString();
    }

    /// <summary>Which ceilings the walk actually ran into. Mutable: it is filled during recursion.</summary>
    private sealed class FolderWalkLimits
    {
        public bool FileCapHit;
        public bool DepthCapHit;
    }

    private static void CollectFolderFiles(
        string dir, List<string> results, int depth, CancellationToken ct, FolderWalkLimits limits)
    {
        if (ct.IsCancellationRequested) return;
        if (depth > MaxWalkDepth) { limits.DepthCapHit = true; return; }
        if (results.Count >= MaxWalkFiles) { limits.FileCapHit = true; return; }
        if (IsSkippedDir(dir)) return;

        try
        {
            // ⚠ Sorted, ordinal: the enumeration order of GetFiles/GetDirectories is the file
            // system's, so it is by name on NTFS and arbitrary on POSIX. The caps downstream keep
            // the FIRST 200 files and attach the body of the FIRST 30 — so without a defined order,
            // WHICH files the model gets is a property of the volume, and the same folder attached
            // twice can yield two different contexts. Same reason RRF got its tie-break: context
            // that wobbles between identical gestures churns the KV-cache prefix, and here it also
            // made two tests measure the platform instead of the product (CI, both POSIX legs).
            foreach (var file in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (ct.IsCancellationRequested) return;
                if (!IndexableExtensions.Contains(Path.GetExtension(file))) continue;
                // ⚠ The ceiling is checked HERE too, not only on entry: a single folder holding
                // five thousand files was listed whole, because the count was only ever consulted
                // between directories. A cap that a common shape walks straight past is not a cap.
                if (results.Count >= MaxWalkFiles) { limits.FileCapHit = true; return; }
                results.Add(file);
            }
            foreach (var subDir in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
                CollectFolderFiles(subDir, results, depth + 1, ct, limits);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("MentionController.CollectFolderFiles", ex); }
    }
}
