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
                if (Directory.Exists(current) || File.Exists(current))
                {
                    var head = ResolveChain(current);
                    return remainder.Length == 0 ? head : Path.Combine(head, remainder);
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

    /// <summary>
    /// Resolves an <b>existing</b> path by following links at <b>every</b> level, not just the last.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>ResolveLinkTarget</c> answers about the component you hand it and says nothing about
    /// its ancestors: on a path whose PARENT is the link it returns <c>null</c>, and the caller
    /// happily concludes "not a link". That is the whole defect (measured 2026-09-10 on the macOS
    /// CI leg): there <c>/var</c> is a symlink to <c>/private/var</c>, so a workspace under
    /// <c>/var/folders/...</c> resolved to itself while a path the product derived from the
    /// process's current directory -- which the kernel hands back already resolved -- came out
    /// under <c>/private/var/...</c>. The two no longer shared a prefix and
    /// <see cref="AssertUnderRoot"/> refused a perfectly legitimate write, with the very message a
    /// user reported for an unrelated reason in issue #9.
    /// <para>
    /// Resolving more of the chain can only make the two sides agree on the <i>real</i> location; it
    /// never widens what the sandbox allows, because the root and the target both go through here.
    /// </para>
    /// </remarks>
    private static string ResolveChain(string existing)
    {
        var hops = MaxLinkHops;
        return ResolveChain(existing, ref hops);
    }

    /// <summary>Total link hops allowed for one resolution, shared by every level of the path.</summary>
    private const int MaxLinkHops = 64;

    private static string ResolveChain(string existing, ref int hops)
    {
        var parent = Path.GetDirectoryName(existing);
        if (string.IsNullOrEmpty(parent) || parent == existing)
            return existing;                       // drive root / filesystem root

        var combined = Path.Combine(ResolveChain(parent, ref hops), Path.GetFileName(existing));
        if (hops <= 0) return combined;            // pathological nesting; stop rather than spin
        try
        {
            FileSystemInfo info = Directory.Exists(combined)
                ? new DirectoryInfo(combined)
                : new FileInfo(combined);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null) return combined;

            // ⚠ The target is resolved AGAIN, and that is not belt-and-braces: on Unix
            // `ResolveLinkTarget` hands back the destination AS RECORDED in the link — .NET reads
            // the link value and combines it with the link's directory, it never realpaths the
            // ancestors. So an absolute recorded target keeps whatever links its own ancestors
            // contain. Measured on the macOS CI leg (2026-09-11), where `/var` is a link to
            // `/private/var`: resolving `<root>/alias` returned `/var/folders/…/real` while the
            // root itself had already come out as `/private/var/folders/…`, the two stopped
            // sharing a prefix, and a legitimate write was refused. ⚠ Windows cannot see this —
            // there `ResolveLinkTarget(returnFinalTarget: true)` goes through
            // GetFinalPathNameByHandle, which canonicalises the whole path — hence the portable
            // test that records the target THROUGH a second link.
            hops--;
            return ResolveChain(target.FullName, ref hops);
        }
        catch (Exception ex)
        {
            // Best-effort, like the caller: an unreadable level keeps the textual form rather than
            // opening a hole.
            Diagnostics.Swallow($"PathSanitizer.ResolveChain({combined})", ex);
            return combined;
        }
    }
}
