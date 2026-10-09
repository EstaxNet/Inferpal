using System.IO;
using System.IO.Enumeration;

namespace Inferpal.Services.Prompting;

/// <summary>The tool a repository wrote its instructions for — what the user turns on or off as a whole.</summary>
internal enum RepoInstructionFamily { Agents, Copilot, Claude, Cursor, Cline, Roo, Continue }

/// <summary>Where a format's files are found, relative to the search root.</summary>
internal enum RepoInstructionPlacement
{
    /// <summary>One file at the root.</summary>
    RootFile,

    /// <summary>One file at the root, then one in each folder down to the active file's (AGENTS.md).</summary>
    Chain,

    /// <summary>The files of a folder at the root.</summary>
    Folder,
}

/// <summary>What a source becomes in the prompt: always-on context, or a rule its front matter may scope.</summary>
internal enum RepoInstructionRole { Context, Rule }

/// <summary>
/// One row of the table of repository instruction formats.
/// </summary>
/// <param name="Location">Relative to the search root, with <c>/</c>: the file, or the folder.</param>
/// <param name="ScopeKey">The front-matter key that scopes a rule to files (<c>applyTo</c>, <c>paths</c>,
/// <c>globs</c>); <c>null</c> when the format has none — always applied.</param>
/// <param name="FilePatterns">For a folder, the file names it holds (<c>*.mdc</c>); a file matching none is not one.</param>
/// <param name="SkipPatterns">For a folder, file names that are never instructions (a log, a backup).</param>
/// <param name="OnlyWithout">The location of another format whose files, when there are any, replace this one —
/// <c>.roorules</c> is read only without rules in <c>.roo/rules/</c>.</param>
internal sealed record RepoInstructionFormat(
    RepoInstructionFamily Family,
    RepoInstructionPlacement Placement,
    string Location,
    RepoInstructionRole Role,
    string? ScopeKey = null,
    IReadOnlyList<string>? FilePatterns = null,
    bool Recursive = false,
    IReadOnlyList<string>? SkipPatterns = null,
    string? OnlyWithout = null)
{
    private readonly string[] _location = Location.Split('/');

    /// <summary>Whether <paramref name="fileName"/> is one of this folder format's files: one of its patterns, none of
    /// its skipped names.</summary>
    internal bool HoldsName(string fileName, bool ignoreCase) =>
        MatchesAny(FilePatterns, fileName, ignoreCase) && !MatchesAny(SkipPatterns, fileName, ignoreCase);

    /// <summary>
    /// Whether a path, cut on its separators, ends at a file of this format — wherever the repository's root is, which
    /// the approval funnel does not know. Case-insensitive: matching more than the file system would costs a prompt,
    /// matching less a silent write.
    /// </summary>
    internal bool EndsAtOneOfItsFiles(IReadOnlyList<string> segments)
    {
        static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
        bool RunAt(int start)
        {
            if (start < 0 || start + _location.Length > segments.Count) return false;
            for (var i = 0; i < _location.Length; i++)
                if (!Same(segments[start + i], _location[i])) return false;
            return true;
        }

        switch (Placement)
        {
            case RepoInstructionPlacement.Chain:                                 // in any folder down the chain
                return segments.Count > 0 && Same(segments[^1], _location[^1]);
            case RepoInstructionPlacement.RootFile:
                return RunAt(segments.Count - _location.Length);
            default:
                if (segments.Count == 0 || !HoldsName(segments[^1], ignoreCase: true)) return false;
                // The folder, then the file — directly, or at any depth below a recursive folder.
                for (var start = segments.Count - 1 - _location.Length; start >= 0; start--)
                {
                    if (RunAt(start)) return true;
                    if (!Recursive) return false;
                }
                return false;
        }
    }

    private static bool MatchesAny(IReadOnlyList<string>? patterns, string fileName, bool ignoreCase) =>
        patterns is not null && patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase));
}

/// <summary>
/// THE table of the instruction files other coding agents read in a repository — one place, read by the discovery,
/// and by everything that must know these files are instructions (the approval forced on writing them).
/// </summary>
/// <remarks>
/// <para>
/// The order of the rows is the order in the prompt at the root: AGENTS.md first, the vendor formats after.
/// </para>
/// <para>
/// Each row follows what the tool that owns the format reads, checked at its source: Cursor reads only <c>.mdc</c> in
/// <c>.cursor/rules</c>; Cline's folder is not recursive and takes <c>.md</c>, <c>.markdown</c> and <c>.txt</c> (its
/// <c>workflows/</c>, <c>hooks/</c> and <c>skills/</c> are not rules); Roo reads every file of <c>.roo/rules</c> but
/// its logs and backups, and <c>.roorules</c> only without them; Continue's folder is not recursive.
/// </para>
/// </remarks>
internal static class RepoInstructionFormats
{
    private static readonly string[] ClineFiles = ["*.md", "*.markdown", "*.txt"];

    // Roo's own exclusions: never instructions, whatever the folder.
    private static readonly string[] RooSkipped =
    [
        "*.DS_Store", "*.bak", "*.cache", "*.crdownload", "*.db", "*.dmp", "*.dump", "*.eslintcache", "*.lock", "*.log",
        "*.old", "*.part", "*.partial", "*.pyc", "*.pyo", "*.stackdump", "*.swo", "*.swp", "*.temp", "*.tmp", "Thumbs.db",
    ];

    public static readonly IReadOnlyList<RepoInstructionFormat> All =
    [
        new(RepoInstructionFamily.Agents,   RepoInstructionPlacement.Chain,    "AGENTS.md",                       RepoInstructionRole.Context),
        new(RepoInstructionFamily.Copilot,  RepoInstructionPlacement.RootFile, ".github/copilot-instructions.md", RepoInstructionRole.Context),
        new(RepoInstructionFamily.Copilot,  RepoInstructionPlacement.Folder,   ".github/instructions",            RepoInstructionRole.Rule,
            ScopeKey: "applyTo", FilePatterns: ["*.instructions.md"], Recursive: true),
        new(RepoInstructionFamily.Claude,   RepoInstructionPlacement.RootFile, "CLAUDE.md",                       RepoInstructionRole.Context),
        new(RepoInstructionFamily.Claude,   RepoInstructionPlacement.RootFile, ".claude/CLAUDE.md",               RepoInstructionRole.Context),
        new(RepoInstructionFamily.Claude,   RepoInstructionPlacement.RootFile, "CLAUDE.local.md",                 RepoInstructionRole.Context),
        new(RepoInstructionFamily.Claude,   RepoInstructionPlacement.Folder,   ".claude/rules",                   RepoInstructionRole.Rule,
            ScopeKey: "paths", FilePatterns: ["*.md"], Recursive: true),
        new(RepoInstructionFamily.Cursor,   RepoInstructionPlacement.Folder,   ".cursor/rules",                   RepoInstructionRole.Rule,
            ScopeKey: "globs", FilePatterns: ["*.mdc"], Recursive: true),
        new(RepoInstructionFamily.Cursor,   RepoInstructionPlacement.RootFile, ".cursorrules",                    RepoInstructionRole.Rule),
        new(RepoInstructionFamily.Cline,    RepoInstructionPlacement.RootFile, ".clinerules",                     RepoInstructionRole.Rule),
        new(RepoInstructionFamily.Cline,    RepoInstructionPlacement.Folder,   ".clinerules",                     RepoInstructionRole.Rule,
            ScopeKey: "paths", FilePatterns: ClineFiles),
        new(RepoInstructionFamily.Cline,    RepoInstructionPlacement.Folder,   ".cline/rules",                    RepoInstructionRole.Rule,
            ScopeKey: "paths", FilePatterns: ClineFiles),
        new(RepoInstructionFamily.Roo,      RepoInstructionPlacement.Folder,   ".roo/rules",                      RepoInstructionRole.Rule,
            FilePatterns: ["*"], Recursive: true, SkipPatterns: RooSkipped),
        new(RepoInstructionFamily.Roo,      RepoInstructionPlacement.RootFile, ".roorules",                       RepoInstructionRole.Rule,
            OnlyWithout: ".roo/rules"),
        new(RepoInstructionFamily.Continue, RepoInstructionPlacement.Folder,   ".continue/rules",                 RepoInstructionRole.Rule,
            ScopeKey: "globs", FilePatterns: ["*.md"]),
    ];

    /// <summary>Whether a path, cut on its separators, ends at a file one of the formats reads — what makes a write
    /// to it a write to some agent's instructions (<c>AgentInstructionFiles</c>).</summary>
    internal static bool AnyEndsAt(IReadOnlyList<string> segments) => All.Any(f => f.EndsAtOneOfItsFiles(segments));

    /// <summary>The name each family goes by in the <c>repoInstructionFamilies</c> setting.</summary>
    internal static readonly IReadOnlyDictionary<string, RepoInstructionFamily> FamilyNames =
        new Dictionary<string, RepoInstructionFamily>(StringComparer.OrdinalIgnoreCase)
        {
            ["agents"]   = RepoInstructionFamily.Agents,
            ["copilot"]  = RepoInstructionFamily.Copilot,
            ["claude"]   = RepoInstructionFamily.Claude,
            ["cursor"]   = RepoInstructionFamily.Cursor,
            ["cline"]    = RepoInstructionFamily.Cline,
            ["roo"]      = RepoInstructionFamily.Roo,
            ["continue"] = RepoInstructionFamily.Continue,
        };

    /// <summary>The families the setting turns on, and the names in it that are none — said, never guessed at.</summary>
    internal static (IReadOnlySet<RepoInstructionFamily> On, IReadOnlyList<string> Unknown) Families(string? setting)
    {
        var on      = new HashSet<RepoInstructionFamily>();
        var unknown = new List<string>();
        foreach (var name in (setting ?? string.Empty).Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (FamilyNames.TryGetValue(name, out var family)) on.Add(family);
            else unknown.Add(name);
        }
        return (on, unknown);
    }

    /// <summary>The product a family is named after, as a person knows it — the same in every language.</summary>
    internal static string ProductName(RepoInstructionFamily family) => family switch
    {
        RepoInstructionFamily.Agents   => "AGENTS.md",
        RepoInstructionFamily.Copilot  => "GitHub Copilot",
        RepoInstructionFamily.Claude   => "Claude Code",
        RepoInstructionFamily.Cursor   => "Cursor",
        RepoInstructionFamily.Cline    => "Cline",
        RepoInstructionFamily.Roo      => "Roo Code",
        _                              => "Continue",
    };
}

/// <summary>A repository instruction file found, with its row of the table.</summary>
internal sealed record RepoInstructionSource(RepoInstructionFormat Format, string Path);

/// <summary>Why instruction files the table names were not taken.</summary>
internal enum RepoInstructionUnseenReason
{
    /// <summary>A folder the process cannot list: its files are unknown.</summary>
    Unlistable,

    /// <summary>A linked folder, not followed: its files are unknown.</summary>
    LinkNotFollowed,

    /// <summary>A link whose target lies outside the search root: not read.</summary>
    LinkLeavesTheRepository,

    /// <summary>More files in the folder than <see cref="RepoInstructionDiscovery.MaxFilesPerFormat"/>.</summary>
    Capped,
}

/// <summary>What the discovery did not take, and why; <paramref name="Count"/> = how many, for a cap.</summary>
internal sealed record RepoInstructionUnseen(string Path, RepoInstructionUnseenReason Reason, int Count = 0);

/// <summary>
/// The repository's own instructions to coding agents, found for one workspace and one active file — in prompt order,
/// with what could not be seen said beside them.
/// </summary>
/// <remarks>
/// Built for each question, like the rest of the system prompt: a file edited, added or removed is in the next one.
/// </remarks>
internal sealed record RepoInstructionDiscovery(
    string? SearchRoot,
    IReadOnlyList<RepoInstructionSource> Sources,
    IReadOnlyList<RepoInstructionUnseen> Unseen)
{
    /// <summary>The most files one folder format contributes; past it, the rest is counted and said.</summary>
    internal const int MaxFilesPerFormat = 50;

    private static readonly RepoInstructionDiscovery None = new(null, [], []);

    // A folder's file names are matched the way the file system compares them.
    private static readonly bool IgnoreCase = !OperatingSystem.IsLinux();

    /// <summary>
    /// The instruction files of the repository holding <paramref name="workspaceRoot"/>, for a question asked with
    /// <paramref name="activeFile"/> open (<c>null</c> = none).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Root-level sources first, in the order of <see cref="RepoInstructionFormats.All"/>; then the AGENTS.md of each
    /// folder from the root down to the active file's — the closest last, the most specific.
    /// </para>
    /// <para>
    /// ⚠ The chain never enters a folder <see cref="WorkspaceScan.IsExcludedDirName"/> excludes: the AGENTS.md of a
    /// package under <c>node_modules</c> is that package's, not the repository's. A rules folder, written by hand, is
    /// read whole (<see cref="WorkspaceScan.EnumerateAll"/>): <c>build/</c> is a topic there.
    /// ⚠ A file whose link leads out of the search root is not read: written by the repository, it would put any file of
    /// the machine in front of the model.
    /// </para>
    /// </remarks>
    public static RepoInstructionDiscovery Discover(string? workspaceRoot, string? activeFile)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return None;
        string workspace;
        try { workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return None; }
        if (!Directory.Exists(workspace)) return None;

        var root = SearchRootFor(workspace, GitProcess.WorkTreeOf(workspace),
                                 Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var sources = new List<RepoInstructionSource>();
        var unseen  = new List<RepoInstructionUnseen>();

        void Take(RepoInstructionFormat format, string path)
        {
            try
            {
                Tools.PathSanitizer.AssertUnderRoot(path, root);
                sources.Add(new RepoInstructionSource(format, path));
            }
            catch (ArgumentException) { unseen.Add(new RepoInstructionUnseen(path, RepoInstructionUnseenReason.LinkLeavesTheRepository)); }
        }

        foreach (var format in RepoInstructionFormats.All)
        {
            var at = Path.Combine(root, format.Location.Replace('/', Path.DirectorySeparatorChar));
            if (format.OnlyWithout is { } replacedBy && sources.Any(s => s.Format.Location == replacedBy)) continue;

            if (format.Placement != RepoInstructionPlacement.Folder)
            {
                if (File.Exists(at)) Take(format, at);
                continue;
            }

            if (!Directory.Exists(at)) continue;
            var files = FolderFiles(format, at);
            if (files.Count > MaxFilesPerFormat)
            {
                unseen.Add(new RepoInstructionUnseen(at, RepoInstructionUnseenReason.Capped, files.Count - MaxFilesPerFormat));
                files = files.Take(MaxFilesPerFormat).ToList();
            }
            foreach (var file in files) Take(format, file);

            if (format.Recursive && WorkspaceScan.FirstWalkGap(at, root) is { } gap)
                unseen.Add(new RepoInstructionUnseen(Path.Combine(root, gap.Folder),
                    gap.Kind == WorkspaceScan.WalkGapKind.NotFollowed
                        ? RepoInstructionUnseenReason.LinkNotFollowed
                        : RepoInstructionUnseenReason.Unlistable));
        }

        var chain = RepoInstructionFormats.All.First(f => f.Placement == RepoInstructionPlacement.Chain);
        foreach (var folder in FoldersDownTo(root, activeFile))
        {
            var at = Path.Combine(folder, chain.Location);
            if (File.Exists(at)) Take(chain, at);
        }

        return new RepoInstructionDiscovery(root, sources, unseen);
    }

    /// <summary>
    /// Where the discovery looks: the git work tree holding <paramref name="workspace"/>, unless reaching it would make a
    /// drive root, or a folder holding <paramref name="home"/>, the search root — then the workspace itself.
    /// </summary>
    /// <remarks>
    /// ⚠ The work tree, not the workspace: Visual Studio's workspace is the folder that holds the solution, usually
    /// <c>src/</c>, while AGENTS.md and <c>.github/</c> sit at the repository's root — the commonest .NET layout.
    /// ⚠ And bounded, the bounds of <c>SolutionExtent</c>: a dotfiles repository at <c>~</c> holds every project of the
    /// user, and its instructions are no project's.
    /// </remarks>
    internal static string SearchRootFor(string workspace, string? workTree, string? home)
    {
        if (string.IsNullOrEmpty(workTree)) return workspace;
        var tree = Path.TrimEndingDirectorySeparator(workTree);
        if (PathComparer.SameDirectory(tree, workspace)) return workspace;
        if (Path.GetDirectoryName(tree) is null) return workspace;                          // a drive root
        if (!string.IsNullOrEmpty(home) && IsSameOrUnder(Path.TrimEndingDirectorySeparator(home), tree)) return workspace;
        return tree;
    }

    /// <summary>The files of a folder format: its patterns, without its skipped names, ordinal order.</summary>
    private static List<string> FolderFiles(RepoInstructionFormat format, string folder)
    {
        IEnumerable<string> all;
        try
        {
            all = format.Recursive
                ? WorkspaceScan.EnumerateAll(folder, "*")
                : Directory.EnumerateFiles(folder, "*", new EnumerationOptions { IgnoreInaccessible = true });
            return all.Where(f => format.HoldsName(Path.GetFileName(f), IgnoreCase))
                      .OrderBy(f => f, StringComparer.Ordinal)
                      .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("RepoInstructionDiscovery.FolderFiles", ex);
            return [];
        }
    }

    /// <summary>
    /// The folders below <paramref name="root"/> down to <paramref name="activeFile"/>'s, outermost first — stopping
    /// before the first one the walks exclude; none when the file is not under the root.
    /// </summary>
    private static IEnumerable<string> FoldersDownTo(string root, string? activeFile)
    {
        if (string.IsNullOrWhiteSpace(activeFile)) yield break;
        string? folder;
        try { folder = Path.GetDirectoryName(Path.GetFullPath(activeFile)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { yield break; }
        if (folder is null || !IsSameOrUnder(folder, root) || PathComparer.SameDirectory(folder, root)) yield break;

        var current = root;
        foreach (var part in Path.GetRelativePath(root, folder).Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (WorkspaceScan.IsExcludedDirName(current)) yield break;
            yield return current;
        }
    }

    private static bool IsSameOrUnder(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(path);
        var f = Path.TrimEndingDirectorySeparator(folder);
        return string.Equals(p, f, PathComparer.Comparison)
            || p.StartsWith(f + Path.DirectorySeparatorChar, PathComparer.Comparison);
    }
}
