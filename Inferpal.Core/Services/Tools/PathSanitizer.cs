using System.IO;
using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

/// <summary>
/// Normalises and validates file-system paths that originate from LLM output.
/// Small models often produce paths with extra whitespace, null bytes, or other
/// characters that cause silent crashes deep in the file-system APIs.
/// </summary>
internal static class PathSanitizer
{
    /// <summary>
    /// Returns a normalised, absolute path, or throws <see cref="ArgumentException"/>
    /// with a localised message the LLM can read and act upon.
    /// </summary>
    internal static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException(Strings.ToolPathRequired);

        // Strip control characters (null bytes, BEL, DEL, …) that small models sometimes inject.
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            if (c >= 0x20 || c == '\t') sb.Append(c);
        var cleaned = sb.ToString().Trim();

        if (string.IsNullOrEmpty(cleaned))
            throw new ArgumentException(Strings.ToolPathRequired);

        try
        {
            // GetFullPath normalises separators, resolves ./ and ../ segments, and
            // throws ArgumentException / PathTooLongException on illegal characters.
            return Path.GetFullPath(cleaned);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new ArgumentException(Strings.ToolPathInvalid(cleaned, ex.Message), ex);
        }
    }

    /// <summary>
    /// Verifies that <paramref name="fullPath"/> (already sanitised) lives under
    /// <paramref name="workspaceRoot"/>. Throws <see cref="ArgumentException"/> if not.
    /// No-ops when <paramref name="workspaceRoot"/> is null or empty (no solution open yet).
    /// </summary>
    internal static void AssertUnderRoot(string fullPath, string? workspaceRoot)
    {
        if (string.IsNullOrEmpty(workspaceRoot)) return;

        // Compare link-resolved paths: Path.GetFullPath normalises ./ and ../ but follows no
        // symlink or junction, so a link planted inside the workspace would otherwise let a
        // write escape it while still passing a textual prefix check.
        var rootBare = Trim(ResolveLinks(Path.GetFullPath(workspaceRoot)));
        var target   = Trim(ResolveLinks(fullPath));

        // The root directory itself is allowed.
        if (string.Equals(target, rootBare, PathComparison)) return;

        // For descendants, require the trailing separator so that "C:\proj\src"
        // doesn't accidentally prefix-match "C:\proj\src_other".
        if (!target.StartsWith(rootBare + Path.DirectorySeparatorChar, PathComparison))
            throw new ArgumentException(
                $"Access denied: path is outside the workspace root.\n" +
                $"  Requested : {fullPath}\n" +
                $"  Workspace : {workspaceRoot}");
    }

    /// <summary>Windows paths are case-insensitive; Linux/macOS ones are not — and the host now
    /// ships for all three (VS Code publishes linux-* and darwin-* builds).</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Trim(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Resolves symlinks/junctions along <paramref name="path"/>. The target itself usually does
    /// not exist yet (a file about to be written), so the deepest existing ancestor is resolved
    /// and the remainder appended back. Best-effort: on any failure the path is returned as-is,
    /// which keeps the caller's textual check in place rather than opening a hole.
    /// </summary>
    private static string ResolveLinks(string path)
    {
        try
        {
            var remainder = string.Empty;
            var current   = path;

            for (var depth = 0; depth < 64; depth++)
            {
                if (Directory.Exists(current))
                {
                    var resolved = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                    var head     = resolved?.FullName ?? current;
                    return remainder.Length == 0 ? head : Path.Combine(head, remainder);
                }
                if (File.Exists(current))
                {
                    var resolved = new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                    return resolved?.FullName ?? current;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current) return path;   // reached the drive root

                remainder = Path.Combine(Path.GetFileName(current), remainder);
                current   = parent;
            }
            return path;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"PathSanitizer.ResolveLinks({path})", ex);
            return path;
        }
    }
}
