namespace Inferpal.Services;

/// <summary>
/// The one answer to "should a workspace walk descend into this directory?".
/// </summary>
/// <remarks>
/// <para>
/// <b>There were seven of these, and no two agreed.</b> The review of 2026-08-07 found a private
/// <c>IsExcluded</c> in <c>ProjectMapService</c>, <c>NexusIntelligenceTool</c>,
/// <c>AnalyzeImpactTool</c>, <c>TraceDependencyTool</c>, <c>RenameSymbolTool</c>,
/// <c>WorkspaceSymbolScanner</c> and <c>MentionController</c>, each with its own list. The
/// divergences were not academic:
/// </para>
/// <list type="bullet">
///   <item><b><c>rename_symbol</c> — the one that writes</b> — compared with
///         <c>StringComparison.Ordinal</c>, so a path containing <c>\Obj\</c> or
///         <c>\Node_Modules\</c> was not excluded at all on a case-insensitive filesystem; and it
///         did not know about <c>.inferpal</c>, so it could rewrite symbols inside the undo
///         snapshots Inferpal keeps of the user's own files.</item>
///   <item>Only two of the seven excluded <c>.inferpal</c>. The others walked
///         <c>.inferpal/history/</c>, which holds COPIES of source files with the same extensions —
///         a project map and an impact analysis built partly on stale duplicates.</item>
///   <item><c>AnalyzeImpactTool</c> excluded <c>\.git\</c> but not <c>/.git/</c>.</item>
/// </list>
/// <para>
/// <b>The list is the union of what the code base already believed</b>, not a fresh opinion:
/// picking a subset would have changed more behaviour than adopting all of it. It stays a
/// heuristic — a project whose real sources live in <c>build/</c> is misjudged here, and the way
/// to tell Inferpal so is <c>.inferpal/project.json</c>, which extends the index exclusions
/// on top of this list.
/// </para>
/// </remarks>
internal static class WorkspaceScan
{
    /// <summary>
    /// Directory names never worth walking: build output, VCS/IDE metadata, dependency caches, and
    /// Inferpal's own data directory.
    /// </summary>
    public static readonly string[] ExcludedDirNames =
    [
        "obj", "bin", "build", "dist",           // build output
        ".git", ".vs", ".generated",             // VCS / IDE / codegen metadata
        "node_modules", "packages",              // dependency caches
        ".venv", "venv",                         // Python virtual environments (a fresh one: ~400 .py of pip alone)
        ".inferpal",                             // Inferpal's own state, incl. history/ snapshots
    ];

    /// <summary>
    /// <c>true</c> when <paramref name="path"/> lies under one of <see cref="ExcludedDirNames"/>
    /// <b>below</b> <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the part below the root is judged. The folders above it are where the user keeps the
    /// workspace, not build output: judged on the absolute path, a workspace under <c>build/</c>,
    /// <c>dist/</c>, <c>bin/</c> or <c>packages/</c> lost every file to every walker. A path that is
    /// not under <paramref name="root"/>, or no root at all, is judged whole.
    /// </para>
    /// <para>
    /// Case-insensitive and separator-agnostic on purpose: both were forgotten by at least one of
    /// the copies this replaces, and on Windows a case-sensitive comparison is simply wrong.
    /// </para>
    /// </remarks>
    public static bool IsExcludedPath(string path, string? root)
    {
        var below = BelowRoot(path, root);
        foreach (var dir in ExcludedDirNames)
        {
            if (below.Contains($@"\{dir}\", StringComparison.OrdinalIgnoreCase) ||
                below.Contains($"/{dir}/",  StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // The part of path below root, keeping its leading separator; the whole path when it is not below.
    private static string BelowRoot(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return path;
        var r = root.TrimEnd('\\', '/');
        return path.Length > r.Length
               && path.StartsWith(r, StringComparison.OrdinalIgnoreCase)
               && path[r.Length] is '\\' or '/'
            ? path[r.Length..]
            : path;
    }

    /// <summary><c>true</c> when a directory should not be descended into, by its own name.</summary>
    /// <remarks>
    /// Separator-agnostic like <see cref="IsExcludedPath"/> — <c>Path.GetFileName</c> alone only
    /// understands the native separator, so on Linux a Windows-style path kept its full
    /// <c>C:\p\node_modules</c> as the "leaf" and was never excluded.
    /// </remarks>
    public static bool IsExcludedDirName(string directoryPath)
    {
        var trimmed = directoryPath.TrimEnd('\\', '/');
        var cut     = trimmed.LastIndexOfAny(['\\', '/']);
        return ExcludedDirNames.Contains(cut < 0 ? trimmed : trimmed[(cut + 1)..],
                                         StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Files matching <paramref name="pattern"/> under <paramref name="start"/>, excluded directories
    /// skipped — judged below <paramref name="root"/> when one is given, below <paramref name="start"/>
    /// otherwise. A start that cannot be opened yields nothing; a folder below it that cannot be read
    /// is skipped instead of ending the walk.
    /// </summary>
    /// <remarks>
    /// The enumeration is lazy, so a <c>try</c> around the call never saw what happened deeper: one
    /// unreadable folder (a database volume owned by a container's user, a locked junction in a
    /// Windows profile) threw inside every caller's loop. <see cref="EnumerationOptions.IgnoreInaccessible"/>
    /// skips it at the source; the other options keep what <c>SearchOption.AllDirectories</c> did
    /// (no attribute skipped, Win32 wildcards).
    /// </remarks>
    public static IEnumerable<string> EnumerateFiles(string start, string pattern = "*.cs", string? root = null)
    {
        var judgedBelow = string.IsNullOrEmpty(root) ? start : root;
        try
        {
            return Directory.EnumerateFiles(start, pattern, WalkOptions)
                            .Where(f => !IsExcludedPath(f, judgedBelow));
        }
        catch { return []; }
    }

    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible    = true,
        AttributesToSkip      = 0,
        MatchType             = MatchType.Win32,
    };
}
