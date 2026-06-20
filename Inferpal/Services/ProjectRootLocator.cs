using System.IO;

namespace Inferpal.Services;

/// <summary>
/// Project-root resolution for an out-of-process extension where CWD ≠ project dir.
/// Order: authoritative solution signal → walk up from each open editor file (≤ 8
/// levels) → .sln walk anchored at CWD (≤ 5 levels, one-level subdir scan, last resort:
/// CWD never follows solution open/close) → parent of the first open file → CWD.
/// File-system probing is injected so the resolution order is unit-testable.
/// </summary>
internal sealed class ProjectRootLocator
{
    private readonly Func<string, bool>                _dirContainsSln;
    private readonly Func<string, IEnumerable<string>> _getSubDirs;

    public ProjectRootLocator(
        Func<string, bool>?                dirContainsSln = null,
        Func<string, IEnumerable<string>>? getSubDirs     = null)
    {
        _dirContainsSln = dirContainsSln
            ?? (dir => Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Length > 0);
        _getSubDirs = getSubDirs ?? Directory.GetDirectories;
    }

    public string Locate(
        IReadOnlyList<string> openPaths,
        string?               activeSolutionDir,
        string                currentDirectory)
    {
        // 0. Authoritative: the in-process package reports the actually-open solution.
        if (activeSolutionDir is not null) return activeSolutionDir;

        // 1. Open editor files — preferred over CWD, which never follows solution open/close.
        var fromOpen = FindSlnDirFromPaths(openPaths);
        if (fromOpen is not null) return fromOpen;

        // 2. A .sln anchored near CWD (last resort).
        var nearCwd = FindSlnDirNearCwd(currentDirectory);
        if (nearCwd is not null) return nearCwd;

        // 3. Final fallback: parent directory of the first open file, or CWD.
        if (openPaths.Count > 0 && !string.IsNullOrEmpty(openPaths[0]))
            return Path.GetDirectoryName(openPaths[0]) ?? currentDirectory;

        return currentDirectory;
    }

    /// <summary>
    /// Same chain as <see cref="Locate"/> but without the unconditional fallbacks:
    /// returns <c>null</c> when no .sln-anchored root can be found yet, so callers that
    /// need a reliable root (e.g. RAG indexing) can retry later.
    /// </summary>
    public string? LocateReliable(
        IReadOnlyList<string> openPaths,
        string?               activeSolutionDir,
        string                currentDirectory)
    {
        if (activeSolutionDir is not null) return activeSolutionDir;
        return FindSlnDirFromPaths(openPaths) ?? FindSlnDirNearCwd(currentDirectory);
    }

    /// <summary>
    /// Walks up (≤ 8 levels) from each open file's directory until a .sln is found.
    /// Reflects the solution actually being edited.
    /// </summary>
    internal string? FindSlnDirFromPaths(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            var dir = Path.GetDirectoryName(p);
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                if (_dirContainsSln(dir))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
        }
        return null;
    }

    /// <summary>
    /// .sln discovery anchored at <paramref name="cwd"/>: at each of ≤ 5 levels, checks
    /// the directory itself then its immediate sub-directories before moving up.
    /// </summary>
    internal string? FindSlnDirNearCwd(string cwd)
    {
        var dir = cwd;
        for (int depth = 0; depth < 5; depth++)
        {
            if (_dirContainsSln(dir))
                return dir;
            foreach (var sub in _getSubDirs(dir))
                if (_dirContainsSln(sub))
                    return sub;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return null;
    }
}
