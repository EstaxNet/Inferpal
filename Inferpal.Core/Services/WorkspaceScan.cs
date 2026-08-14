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
/// to tell Inferpal so is <c>.inferpal/project.json</c> (§19), which extends the index exclusions
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
        ".inferpal",                             // Inferpal's own state, incl. history/ snapshots
    ];

    /// <summary>
    /// <c>true</c> when <paramref name="path"/> lies under one of
    /// <see cref="ExcludedDirNames"/>.
    /// </summary>
    /// <remarks>
    /// Case-insensitive and separator-agnostic on purpose: both were forgotten by at least one of
    /// the copies this replaces, and on Windows a case-sensitive comparison is simply wrong.
    /// </remarks>
    public static bool IsExcludedPath(string path)
    {
        foreach (var dir in ExcludedDirNames)
        {
            if (path.Contains($@"\{dir}\", StringComparison.OrdinalIgnoreCase) ||
                path.Contains($"/{dir}/",  StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary><c>true</c> when a directory should not be descended into, by its own name.</summary>
    public static bool IsExcludedDirName(string directoryPath) =>
        ExcludedDirNames.Contains(Path.GetFileName(directoryPath.TrimEnd('\\', '/')),
                                  StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Files matching <paramref name="pattern"/> under <paramref name="root"/>, excluded
    /// directories skipped. Returns empty rather than throwing on an unreadable tree.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string root, string pattern = "*.cs")
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                            .Where(f => !IsExcludedPath(f));
        }
        catch { return []; }
    }
}
