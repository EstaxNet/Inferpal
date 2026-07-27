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

        // Normalise the root: resolve any ./ ../ and strip any trailing separator.
        var rootBare = Path.GetFullPath(workspaceRoot)
                           .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // The root directory itself is allowed.
        if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                           rootBare, StringComparison.OrdinalIgnoreCase))
            return;

        // For descendants, require the trailing separator so that "C:\proj\src"
        // doesn't accidentally prefix-match "C:\proj\src_other".
        var root = rootBare + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Access denied: path is outside the workspace root.\n" +
                $"  Requested : {fullPath}\n" +
                $"  Workspace : {workspaceRoot}");
    }
}
