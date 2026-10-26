using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Localization;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Presentation;

/// <summary>Context-provider categories offered when typing <c>@</c> in the chat prompt.</summary>
internal enum MentionKind { File, Code, Folder, Clipboard, Tree, Diff, Problems, Debugger, Url, Commit }

/// <summary>What a mention resolves to: a chip (label and content), or a notice when there is nothing to attach.</summary>
internal readonly record struct MentionOutcome(string? Label, string? Content, string? Notice);

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
    // (e.g. "@file Foo", "@folder Serv", "@code auth logic", "@url docs.rs"). Only file/code/folder/url/commit drill
    // down; the instant providers (clipboard/tree/diff/problems) attach straight from the category menu.
    // ⚠ An address or a revision holds no space, and may hold an '@' (npmjs.com/package/@types/node, HEAD@{1}): its value
    // ends at the first space — past it, the rest of the line is the question, which picking the row would strip.
    internal static readonly Regex MentionQueryRegex =
        new(@"@(?:(?<cat>file|code|folder)[ ](?<q>[^\n@]*)|(?<cat>url|commit)[ ](?<q>\S*))$",
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
        new("@url",       MentionKind.Url,       () => Strings.MentionUrlDesc,       QueryBased: true),
        new("@commit",    MentionKind.Commit,    () => Strings.MentionCommitDesc,    QueryBased: true),
    ];

    /// <summary>File extensions eligible for <c>@file</c> search and folder context bodies.</summary>
    /// <remarks>⚠ Everything the index reads (<see cref="Rag.CodeChunker.SupportedExtensions"/>) and the common text
    /// formats besides: with <c>.c</c> and <c>.rs</c> missing, <c>@folder</c> on a C project listed only its headers and a
    /// Rust crate an empty "Files:" — read as "this folder holds no source file".</remarks>
    internal static readonly HashSet<string> IndexableExtensions =
        new(Rag.CodeChunker.SupportedExtensions.Concat(
            [
                ".cs", ".ts", ".js", ".tsx", ".jsx", ".mjs", ".cjs", ".mts", ".cts", ".py", ".go", ".java",
                ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".rs", ".razor", ".cshtml", ".vue", ".svelte", ".fs", ".vb",
                ".kt", ".kts", ".swift", ".scala", ".rb", ".php", ".dart", ".lua", ".r", ".m", ".mm", ".ex", ".exs",
                ".sql", ".sh", ".bash", ".ps1", ".psm1", ".bat", ".cmd", ".html", ".htm", ".css", ".scss", ".less",
                ".json", ".xml", ".xaml", ".yaml", ".yml", ".toml", ".ini", ".md", ".txt", ".config", ".proto", ".graphql",
                ".gradle", ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".props", ".targets", ".tf",
            ]), StringComparer.OrdinalIgnoreCase);

    /// <summary>Files read by NAME, having no extension that says what they are.</summary>
    private static readonly HashSet<string> IndexableNames =
        new(StringComparer.OrdinalIgnoreCase) { "Dockerfile", "Makefile", "Jenkinsfile", "Gemfile", "Rakefile", "CMakeLists.txt" };

    internal static bool IsIndexable(string file) =>
        IndexableExtensions.Contains(Path.GetExtension(file)) || IndexableNames.Contains(Path.GetFileName(file));

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

    /// <summary>
    /// What <c>@diff</c> attaches, decided on the git report's state, never on its text: the diff — its chip named as cut
    /// when the report holds part of it — or a notice when there is nothing to attach.
    /// </summary>
    /// <remarks>
    /// ⚠ Outside a repository, with git refusing, or with no change, the report is still a report: attached, it made a
    /// chip that held no diff, and the question went to the model as if it carried one. And the diff is cut to one tool
    /// result: the note that says so is written for the model, so the chip says it to the person.
    /// </remarks>
    internal static MentionOutcome DiffMention(GetGitStatusTool.Report report, string label) => report.Outcome switch
    {
        GetGitStatusTool.State.NotARepository or GetGitStatusTool.State.Refused
            => new MentionOutcome(null, null, Strings.MentionDiffUnavailable(report.Refusal ?? report.Text)),
        GetGitStatusTool.State.Clean => new MentionOutcome(null, null, Strings.MentionDiffEmpty),
        _ => new MentionOutcome(
            report.DiffCut ? Strings.MentionDiffCutLabel(label, report.DiffShown, report.DiffTotal) : label, report.Text, null),
    };

    /// <summary>
    /// What <c>@url</c> attaches: the page's readable text, fetched by <c>fetch_url</c>'s network path — its refusal of
    /// private addresses and its re-checked redirects included — or a notice saying why there is none.
    /// </summary>
    /// <param name="notAllowed">Why the person's settings refuse fetching the address (<c>fetch_url</c> turned off, a deny
    /// rule: <see cref="Execution.ToolRegistry.PersonFetchRefusal"/>), or <c>null</c>.</param>
    /// <remarks>⚠ No approval card: the address is the one the person typed, and the card guards against a model sending
    /// data out. But the switch and the deny rules are the person's own: a fetch they turned off stays off. A binary page
    /// is a notice, never a chip whose content says it is binary.</remarks>
    internal static async Task<MentionOutcome> UrlMentionAsync(string typed, Func<string, string?> notAllowed, CancellationToken ct)
    {
        var url = typed.Trim();
        if (url.Length == 0) return new(null, null, Strings.MentionNothingToAttach("url"));
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (FetchUrlTool.Refusal(url) is { } refused) return new(null, null, Strings.MentionUrlUnavailable(refused));
        if (notAllowed(url) is { } off) return new(null, null, off);

        WebPage.WebText page;
        try { page = await FetchUrlTool.FetchAsync(url, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(null, null, Strings.MentionUrlUnavailable(Diagnostics.RootMessage(ex))); }

        var text = FetchUrlTool.Readable(page);
        if (page.Binary) return new(null, null, Strings.MentionUrlUnavailable(text));
        if (string.IsNullOrWhiteSpace(text)) return new(null, null, Strings.MentionUrlEmpty(url));
        var shown = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host + u.AbsolutePath.TrimEnd('/') : url;
        return new("🌐 " + ChatTurnPolicy.OneLinePreview(shown, 48), $"{url}\n\n{text}", null);
    }

    /// <summary>
    /// The commits <c>@commit</c> offers under the line that attaches the typed revision: the latest ones whose hash
    /// starts with <paramref name="query"/> or whose subject holds it, each valued by its full object name.
    /// </summary>
    /// <remarks>Only the latest 200 are looked through: an older commit is attached by its hash, branch or tag, through the
    /// typed line. Nothing on a folder outside a repository or a git that refused: the typed line says why.</remarks>
    internal static async Task<IReadOnlyList<(string Short, string Subject, string Sha)>> FindCommitsAsync(
        string? root, string query, CancellationToken ct)
    {
        if (await WorkTreeAsync(root, ct) is not { } workTree) return [];
        var run = await GitProcess.For(workTree)("log -n 200 --no-show-signature --no-color --format=%H%x09%h%x09%s", ct);
        if (run.ExitCode != 0) return [];
        var q = query.Trim();
        return run.Output.Split('\n')
            .Select(l => l.TrimEnd('\r').Split('\t', 3))
            .Where(p => p.Length == 3 && p[0].Length >= 40)
            .Where(p => q.Length == 0 || p[0].StartsWith(q, StringComparison.OrdinalIgnoreCase)
                                      || p[2].Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .Select(p => (p[1], p[2], p[0]))
            .ToList();
    }

    /// <summary>
    /// What <c>@commit</c> attaches: one commit's message and its diff against its first parent, the files the workspace's
    /// ignore files exclude left out and named — or a notice when there is no such commit to read.
    /// </summary>
    /// <remarks>
    /// ⚠ One commit, never a range: <c>a..b</c> is refused saying so. The revision is read by <see cref="Commands.CheckTarget"/> —
    /// the person's text never reaches git as an option, a name two refs share is refused naming them — and the diff is
    /// <c>/check</c>'s: a root commit against nothing, a merge against its first parent, the edge of a shallow clone said.
    /// </remarks>
    internal static async Task<MentionOutcome> CommitMentionAsync(string? root, string typed, CancellationToken ct)
    {
        var revision = typed.Trim() is { Length: > 0 } t ? t : "HEAD";
        if (await WorkTreeAsync(root, ct) is not { } workTree)
            return new(null, null, Strings.MentionCommitUnavailable(Strings.GitNotRepo));
        if (revision.Contains("..", StringComparison.Ordinal)) return new(null, null, Strings.MentionCommitRange(revision));
        if (!Commands.CheckTarget.IsRevisionText(revision)) return new(null, null, Strings.MentionCommitUnknown(revision));

        var git = GitProcess.For(workTree);
        var (sha, why) = await Commands.CheckTarget.CommitOrWhyAsync(revision, git, ct);
        if (why is not null) return new(null, null, Strings.MentionCommitUnavailable(why));
        if (sha is null)
            // No HEAD at all is a repository without a commit yet, never a name to type better.
            return new(null, null, (await git("rev-parse --verify --quiet HEAD", ct)).ExitCode == 0
                ? Strings.MentionCommitUnknown(revision)
                : Strings.MentionCommitNoneYet);
        // Resolved by its object name, never the person's text: a branch would be read as what it adds to HEAD.
        var (target, nothing) = await Commands.CheckTarget.ResolveAsync(sha, git, ct);
        if (target is null) return new(null, null, Strings.MentionCommitUnavailable(nothing ?? Strings.GitNotRepo));

        var log = $"log -1 --no-show-signature --no-decorate --no-color --format=medium {sha}";
        var message = await git(log, ct);
        if (GitProcess.FailureNote(log, message) is { } logFailed) return new(null, null, Strings.MentionCommitUnavailable(logFailed));
        var diff = await git(target.DiffArgs, ct);
        if (GitProcess.FailureNote(target.DiffArgs, diff) is { } diffFailed)
            return new(null, null, Strings.MentionCommitUnavailable(diffFailed));

        // The lines of an excluded file are its content: its sections leave the diff, and the note above says so.
        var excluded = new Governance.ContentExclusions.Tally();
        var readable = Governance.ContentExclusions.WithoutExcludedFiles(diff.Output, workTree, excluded).TrimEnd();
        var content  = new StringBuilder(message.Output.TrimEnd()).Append("\n\n");
        if (excluded.Count > 0) content.Append(excluded.Note()).Append('\n');
        content.Append(readable.Length > 0 ? readable : excluded.Count > 0 ? "" : "(no change against its first parent)");
        return new($"📜 @commit {target.Shown}", content.ToString().TrimEnd(), null);
    }

    /// <summary>The git work tree that holds <paramref name="root"/>, or <c>null</c> outside one.</summary>
    /// <remarks>Asked of git when no <c>.git</c> is found above the folder as written: a workspace that is a link into a
    /// repository is in it.</remarks>
    private static async Task<string?> WorkTreeAsync(string? root, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(root)) return null;
        if (GitProcess.WorkTreeOf(root) is { } found) return found;
        var top = await GitProcess.For(root)("rev-parse --show-toplevel", ct);
        return top.ExitCode == 0 && GitProcess.FirstLine(top.Output) is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// What <c>@problems</c> attaches in Visual Studio, decided on the build verdict's single reader: the build's answer,
    /// or — when no build ran (no project to build, a build stopped at its time limit, a path refused) — a notice saying
    /// why, never a chip whose text is that reason.
    /// </summary>
    /// <remarks>⚠ Attached as it came, "no project found" left a "⚠ @problems" chip, and the question went to the model as
    /// if it carried the build's errors.</remarks>
    internal static MentionOutcome ProblemsMention(string diagnostics, string label) =>
        GetDiagnosticsTool.ReadVerdict(diagnostics) == GetDiagnosticsTool.BuildVerdict.NotBuilt
            ? new MentionOutcome(null, null, Strings.MentionProblemsNotBuilt(ChatTurnPolicy.OneLinePreview(diagnostics, 240)))
            : new MentionOutcome(label, diagnostics, null);

    /// <summary>
    /// What <c>@code</c> attaches, decided on the search's hit count, never on its text: the results, or — when the search
    /// found nothing — a notice saying so.
    /// </summary>
    /// <remarks>⚠ Attached as it came, "no results" made a 🔮 chip, and the question went to the model as if it carried
    /// the code it was about.</remarks>
    internal static MentionOutcome CodeMention(Tools.SemanticSearchTool.Answer answer, string label) =>
        answer.Hits == 0
            ? new MentionOutcome(null, null, Strings.MentionCodeNothingFound(answer.NothingFound ?? answer.Text))
            : new MentionOutcome(label, answer.Text, null);

    /// <summary>
    /// What <c>@tree</c> attaches: the project map, or — when there is no solution or project root to map — a notice,
    /// never a 🌲 chip whose content is that sentence.
    /// </summary>
    internal static MentionOutcome TreeMention(string? map, string label) =>
        map is null
            ? new MentionOutcome(null, null, Strings.MentionTreeNoRoot)
            : new MentionOutcome(label, map, null);

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
        var visited = 0;
        CollectFiles(rootDir, queryLower, results, 0, ref visited, ct);
        return Rank(results);
    }

    /// <summary>Same as <see cref="FindFiles"/> for directories (empty query lists everything).</summary>
    public static IReadOnlyList<string> FindFolders(string rootDir, string queryLower, CancellationToken ct)
    {
        var results = new List<(string Path, int Score)>();
        var visited = 0;
        CollectFolders(rootDir, queryLower, results, 0, ref visited, ct);
        return Rank(results);
    }

    private static IReadOnlyList<string> Rank(List<(string Path, int Score)> results) =>
        results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => Path.GetFileName(r.Path), StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(r => r.Path)
            .ToList();

    /// <summary>Entries a search walks at most — a bound on the WORK, never on the matches.</summary>
    /// <remarks>
    /// ⚠ The ranking comes after the walk: a walk stopped at a number of MATCHES keeps the first ones it meets — a
    /// hundred names that merely contain the query, found first, and the file named exactly as typed is never collected,
    /// the picker offering eight lesser matches. Each level is read in ordinal order, so the bound cuts the same tree
    /// everywhere.
    /// </remarks>
    internal const int MaxVisitedEntries = 20_000;

    private static void CollectFiles(
        string dir, string query, List<(string, int)> results, int depth, ref int visited, CancellationToken ct)
    {
        if (depth > 8 || ct.IsCancellationRequested || visited >= MaxVisitedEntries) return;
        if (IsSkippedDir(dir)) return;

        try
        {
            foreach (var file in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (ct.IsCancellationRequested || ++visited > MaxVisitedEntries) return;
                if (!IsIndexable(file)) continue;

                var name = Path.GetFileName(file).ToLowerInvariant();
                if (!name.Contains(query)) continue;

                var score = name.StartsWith(query, StringComparison.Ordinal) ? 2 : 1;
                if (name == query || name == query + Path.GetExtension(name)) score = 3;
                results.Add((file, score));
            }

            foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
                CollectFiles(sub, query, results, depth + 1, ref visited, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("MentionController.CollectFiles", ex); }
    }

    private static void CollectFolders(
        string dir, string query, List<(string, int)> results, int depth, ref int visited, CancellationToken ct)
    {
        if (depth > 6 || ct.IsCancellationRequested || visited >= MaxVisitedEntries) return;

        try
        {
            foreach (var subDir in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (ct.IsCancellationRequested || ++visited > MaxVisitedEntries) return;
                if (IsSkippedDir(subDir)) continue;

                var nl = Path.GetFileName(subDir).ToLowerInvariant();
                if (query.Length == 0 || nl.Contains(query))
                {
                    var score = nl.StartsWith(query, StringComparison.Ordinal) ? 2 : 1;
                    if (nl == query) score = 3;
                    results.Add((subDir, score));
                }
                CollectFolders(subDir, query, results, depth + 1, ref visited, ct);
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

    /// <summary>Characters the list of files leaves free for the notes that come after it.</summary>
    private const int ListReserve = 400;

    /// <summary>
    /// The folder an <c>@folder</c> value names: resolved against the workspace root, never the process's directory.
    /// </summary>
    public static string FolderPath(string value, string? root) =>
        Path.IsPathFullyQualified(value) || string.IsNullOrEmpty(root) ? Path.GetFullPath(value) : Path.GetFullPath(value, root);

    /// <summary>
    /// Why <c>@folder</c> attaches nothing — the notice shown in place of a chip — or <c>null</c> when there is a folder
    /// to walk.
    /// </summary>
    /// <remarks>
    /// ⚠ A chip is a claim that something joined the question. Under a 📁 chip, a typo read "this folder could not be
    /// listed — it may have been removed", and a FILE read the same: the user believes the folder is attached, the model
    /// receives a note in its place. A folder that exists but cannot be read, or one the walk skips by name, keeps its
    /// chip — its note is about that folder, and says so (<see cref="BuildFolderContext"/>).
    /// </remarks>
    public static string? FolderRefusal(string folderPath, string? root = null) =>
        File.Exists(folderPath)       ? Strings.MentionFolderIsFile(Path.GetFileName(folderPath))
        : !Directory.Exists(folderPath) ? Strings.DirNotFound(folderPath)
        : Governance.ContentExclusions.Of(folderPath, isFolder: true) is { } why
            ? Strings.ExcludedFromAi(Governance.ContentExclusions.Shown(folderPath, root), why.File, why.Line, why.Pattern)
        : null;

    /// <summary>
    /// Why <c>@file</c> — or any attachment of a file's content — attaches nothing because the workspace's ignore files
    /// exclude it, or <c>null</c>; the notice names the file, the ignore file, the line and the pattern.
    /// </summary>
    public static string? FileRefusal(string filePath, string? root) => Governance.ContentExclusions.NoticeFor(filePath, root);

    /// <summary>
    /// Why a file attaches no text because it is binary (an image, an assembly, a key), or <c>null</c> — never its bytes
    /// decoded as text, which spend the window on garbage the model reads as code.
    /// </summary>
    public static string? BinaryRefusal(string filePath)
    {
        try { return Tools.TextFileEncoding.IsBinaryFile(filePath) ? Strings.AttachBinaryFile(Path.GetFileName(filePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Why <c>@code</c> attaches nothing — the notice shown in place of a chip — or <c>null</c> when there is an index to
    /// search. An empty index answers "not available": under a 🔮 chip that sentence passes for the code found.
    /// </summary>
    public static string? CodeRefusal(Rag.ProjectIndexService index) =>
        index.ChunkCount == 0 ? Strings.RagIndexNotReady(index.Status) : null;

    /// <summary>The largest file an <c>@file</c> attaches, in bytes on disk or characters in the editor.</summary>
    internal const int MaxAttachedFileSize = 512_000;

    /// <summary>
    /// The text an <c>@file</c> attaches — the editor's unsaved buffer when it holds the file, as <c>read_file</c> reads
    /// it, else the disk — or <c>null</c> when it is over <see cref="MaxAttachedFileSize"/>.
    /// </summary>
    /// <remarks>⚠ Read from the disk, a file with unsaved changes was attached as it was before them: the question was
    /// about the code on the screen, the answer about the code saved.</remarks>
    /// <param name="overlay">The editor's unsaved buffers, refreshed by the caller; null when the editor mirrors none.</param>
    public static async Task<string?> FileMentionTextAsync(string path, Editor.OpenDocumentOverlay? overlay, CancellationToken ct)
    {
        if (overlay is not null && overlay.TryGetUnsaved(path, out var buffered))
            return buffered.Length > MaxAttachedFileSize ? null : buffered;
        if (new FileInfo(path).Length > MaxAttachedFileSize) return null;
        // Decoded like the file an edit rewrites: an attached line is what the model quotes back into one.
        return await Tools.TextFileEncoding.ReadTextAsync(path, ct).ConfigureAwait(false);
    }

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
    /// <param name="overlay">The editor's unsaved buffers (VS Code), read instead of the disk for the files they hold —
    /// as read_file shows them; null when the editor mirrors none.</param>
    /// <param name="maxChars">The bodies' budget; smaller than the default for a reader whose window is (a code action) —
    /// given here, never cut after, or the notes below the bodies, which say what is missing, are what goes.</param>
    public static string BuildFolderContext(string folderPath, CancellationToken ct, Editor.OpenDocumentOverlay? overlay = null,
                                            int maxChars = MaxTotalChars)
    {
        var sb = new StringBuilder();
        sb.Append("Folder: ").AppendLine(folderPath).AppendLine();

        var files  = new List<string>();
        var limits = new FolderWalkLimits();
        CollectFolderFiles(folderPath, files, 0, ct, limits);

        sb.AppendLine("Files:");
        // ⚠ The list counts in the budget too: a folder of a few hundred deep paths passes it by itself, and the
        // attachment then outgrows the window it was sized for. It stops where the budget would, and says so below.
        var listed = 0;
        foreach (var f in files)
        {
            var line = "  " + Path.GetRelativePath(folderPath, f);
            if (sb.Length + line.Length + ListReserve > maxChars) break;
            sb.AppendLine(line);
            listed++;
        }
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
            try
            {
                body = overlay is not null && overlay.TryGetUnsaved(f, out var buffered)
                    ? buffered
                    : Tools.TextFileEncoding.ReadText(f);
            }
            catch { unreadable++; continue; }

            var header = $"\n----- {Path.GetRelativePath(folderPath, f)} -----\n";
            if (sb.Length + header.Length + body.Length > maxChars)
            {
                var budget = maxChars - sb.Length - header.Length;
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
        if (listed < files.Count)
            notes.Add($"(the list stops at {listed} of {files.Count} files: the {maxChars:N0}-character budget — the rest are "
                    + "NOT named above)");
        if (files.Count > MaxFiles)
            notes.Add($"(bodies: the first {MaxFiles} of {files.Count} listed files — the rest are named above but NOT included)");
        if (budgetStop)
            notes.Add($"(stopped at the {maxChars:N0}-character budget: {attempted - included - unreadable} more listed file(s) not included)");
        if (unreadable > 0)
            notes.Add($"({unreadable} file(s) could not be read and are not included)");
        if (limits.FileCapHit)
            notes.Add($"(the folder walk stopped at {MaxWalkFiles} files — this folder holds more, and they are not listed above)");
        if (limits.DepthCapHit)
            notes.Add($"(subfolders deeper than {MaxWalkDepth} levels were not listed)");
        // ⚠ These three do not narrow the answer, they INVALIDATE it: with an empty "Files:" block
        // and no note, the context reads as "this folder holds no source file".
        if (limits.RootUnlistable)
            notes.Add("(this folder could not be listed at all — it may have been removed, or this "
                    + "process may not be allowed to read it. The list above is NOT a statement "
                    + "about what the folder contains.)");
        if (limits.RootSkipped)
            notes.Add("(this folder is one the walk skips by name — build output, dependencies or "
                    + "VCS data — so nothing under it was listed)");
        if (limits.UnlistableSub is { } sub)
            notes.Add($"(the subfolder {Path.GetRelativePath(folderPath, sub)} could not be listed — "
                    + "its files are missing from the list above)");
        // ⚠ Files of a type this attachment does not read are left out of the list: said, or a folder of them reads as
        // empty.
        if (limits.OtherTypes.Count > 0)
            notes.Add($"({limits.OtherTypes.Values.Sum()} file(s) of other types are not listed: "
                    + string.Join(", ", limits.OtherTypes.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal)
                                                     .Take(5).Select(t => $"{t.Key} ×{t.Value}"))
                    + (limits.OtherTypes.Count > 5 ? ", …" : string.Empty) + ")");
        // ⚠ And the ones the workspace keeps out of reach: unlisted, a file the model is asked about reads as absent.
        if (limits.Excluded.Count > 0)
            notes.Add($"({limits.Excluded.Count} file(s) excluded from AI tools by {limits.Excluded.Rules()} are not listed "
                    + "nor read — they exist)");

        if (notes.Count > 0) sb.Append('\n').AppendLine().AppendJoin('\n', notes);
        return sb.ToString();
    }

    /// <summary>Which ceilings the walk actually ran into. Mutable: it is filled during recursion.</summary>
    /// <remarks>
    /// ⚠ The last two are not ceilings but <b>failures</b>, and they belong here for the same
    /// reason: they empty the listing without emptying the folder. A caller that reports the five
    /// cuts and not these hands the model "Files:" with nothing under it, which reads as "this
    /// folder holds no source file" — the one sentence the folder cannot answer.
    /// </remarks>
    private sealed class FolderWalkLimits
    {
        public bool FileCapHit;
        public bool DepthCapHit;

        /// <summary>The root itself could not be listed: removed, or not readable here.</summary>
        public bool RootUnlistable;

        /// <summary>The root is one the walk skips by name (build output, dependencies, VCS data).</summary>
        public bool RootSkipped;

        /// <summary>First sub-directory that could not be listed — named, never counted: how many
        /// files it held is precisely what nobody can know.</summary>
        public string? UnlistableSub;

        /// <summary>Files left out because of their type, counted per extension ("(none)" for a name without one).</summary>
        public readonly Dictionary<string, int> OtherTypes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Files the workspace's ignore files exclude — neither listed nor read.</summary>
        public readonly Governance.ContentExclusions.Tally Excluded = new();
    }

    private static void CollectFolderFiles(
        string dir, List<string> results, int depth, CancellationToken ct, FolderWalkLimits limits)
    {
        if (ct.IsCancellationRequested) return;
        if (depth > MaxWalkDepth) { limits.DepthCapHit = true; return; }
        if (results.Count >= MaxWalkFiles) { limits.FileCapHit = true; return; }
        if (IsSkippedDir(dir))
        {
            // Only at the root: a skipped SUBfolder is the ordinary case this walk exists to do,
            // while a skipped root means the whole answer is empty for a reason the user can act on.
            if (depth == 0) limits.RootSkipped = true;
            return;
        }

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
                if (!IsIndexable(file))
                {
                    var type = Path.GetExtension(file) is { Length: > 0 } e ? e.ToLowerInvariant() : "(none)";
                    limits.OtherTypes[type] = limits.OtherTypes.GetValueOrDefault(type) + 1;
                    continue;
                }
                // ⚠ The ceiling is checked HERE too, not only on entry: a single folder holding
                // five thousand files was listed whole, because the count was only ever consulted
                // between directories. A cap that a common shape walks straight past is not a cap.
                if (results.Count >= MaxWalkFiles) { limits.FileCapHit = true; return; }
                // Excluded by the workspace's ignore files: neither listed nor read, counted for the note.
                if (Governance.ContentExclusions.Of(file) is { } why) { limits.Excluded.Add(file, why); continue; }
                results.Add(file);
            }
            foreach (var subDir in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
                CollectFolderFiles(subDir, results, depth + 1, ct, limits);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // ⚠ The trace stays (it carries the exception), but it is not the channel: the reader
            // of this context is a model, and it never sees /diagnostics. The fact travels with the
            // other cuts instead.
            Diagnostics.Swallow("MentionController.CollectFolderFiles", ex);
            if (depth == 0) limits.RootUnlistable = true;
            else            limits.UnlistableSub ??= dir;
        }
    }
}
