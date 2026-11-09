using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

/// <summary>A path refused because no workspace is open: no other path, and no retry, gets through.</summary>
/// <remarks>An <see cref="ArgumentException"/>, so that every caller that catches a path outside the root catches this one
/// too; told apart by <c>ToolFailure</c>, which must not advise the model to check its arguments and try again.</remarks>
internal sealed class NoWorkspaceRootException() : ArgumentException(PathSanitizer.NoWorkspaceRoot);

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
    internal static string Sanitize(string? raw) => Sanitize(raw, workspaceRoot: null);

    /// <summary>
    /// Like <see cref="Sanitize(string?)"/>, but a path that is not fully qualified resolves against
    /// <paramref name="workspaceRoot"/>, never against the process's working directory.
    /// </summary>
    /// <remarks>
    /// ⚠ In Visual Studio the working directory is not the project: the out-of-process extension
    /// host keeps the directory it started in for its whole lifetime (see
    /// <c>ActiveSolutionSignal</c>). A relative path — <c>src/Foo.cs</c>, <c>.</c> — therefore
    /// resolved outside the workspace and was refused as "outside the workspace root", while the
    /// same call worked in VS Code, whose host is spawned in the workspace folder.
    /// <see cref="AssertUnderRoot"/> is unchanged: a <c>..</c> that climbs out is still refused.
    /// Without a root (no solution open yet) the working directory is the only base there is.
    /// </remarks>
    internal static string Sanitize(string? raw, string? workspaceRoot)
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
        // ⚠ A Windows path on Linux or macOS names nothing there: resolved anyway, against the process's folder, it named a
        // file literally called `C:\x\y.cs` — inside the workspace for the VS Code host, which runs in it.
        if (!OperatingSystem.IsWindows() && IsForeignAbsolute(cleaned))
            throw new ArgumentException(Strings.ToolPathInvalid(cleaned, "a Windows path, on a system without drives or shares"));

        string full;
        try
        {
            // GetFullPath normalises separators, resolves ./ and ../ segments, and
            // throws ArgumentException / PathTooLongException on illegal characters.
            full = !string.IsNullOrEmpty(workspaceRoot) && !Path.IsPathFullyQualified(cleaned) && !IsForeignAbsolute(cleaned)
                ? Path.GetFullPath(cleaned, Path.GetFullPath(workspaceRoot))
                : Path.GetFullPath(cleaned);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new ArgumentException(Strings.ToolPathInvalid(cleaned, ex.Message), ex);
        }
        return OperatingSystem.IsWindows() ? WithoutStreamName(full) : full;
    }

    /// <summary>
    /// A Windows path as the file it writes: the data stream suffix <c>::$DATA</c> taken off (it IS the file), any other
    /// stream after the root refused.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>.env::$DATA</c> writes <c>.env</c>: kept as written, every reader of the path as TEXT — a <c>deny … \.env$</c>
    /// rule, the forced prompt of the agent's instruction files, the snapshot's name — saw another file. And an alternate
    /// stream (<c>Cart.cs:hidden</c>) holds content no diff, no editor and no build shows: no tool writes there.
    /// </remarks>
    internal static string WithoutStreamName(string fullPath)
    {
        var rootLength = Path.GetPathRoot(fullPath)?.Length ?? 0;
        var path = fullPath;
        while (path.EndsWith("::$DATA", StringComparison.OrdinalIgnoreCase)) path = path[..^"::$DATA".Length];
        if (path.IndexOf(':', rootLength) >= 0)
            throw new ArgumentException(Strings.ToolPathInvalid(fullPath, "a ':' after the drive names an NTFS stream, not a file"));
        return path;
    }

    /// <summary>
    /// A path that is absolute on ANOTHER platform: a drive (<c>C:\</c>, <c>C:/</c>) or a UNC share
    /// (<c>\\server</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ On Linux and macOS neither is fully qualified, so without this check a Windows path the
    /// model wrote out of habit would resolve INSIDE the workspace — a file literally named
    /// <c>C:\elsewhere\Other.cs</c> under the root, accepted where it has always been refused.
    /// </remarks>
    internal static bool IsForeignAbsolute(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>The refusal for a path asked while no workspace is open: it names the gesture that gives one.</summary>
    internal const string NoWorkspaceRoot =
        "Access denied: no workspace is known, and Inferpal reads and writes files only inside one. "
        + "Open a folder or a solution in the editor — if one is already open, open one of its files; a whole drive or "
        + "your user folder is not used as a workspace — then ask again.";

    /// <summary>
    /// Verifies that <paramref name="fullPath"/> (already sanitised) lives under
    /// <paramref name="workspaceRoot"/>. Throws <see cref="ArgumentException"/> if not.
    /// </summary>
    /// <remarks>
    /// ⚠ Without a root, nothing is under it. Accepting every path when the root is empty lets the model read and write
    /// anywhere on the disk whenever Visual Studio has no workspace yet, the deny overlay and the ignore files unread. A
    /// caller for which "no root" means something else decides it before calling.
    /// </remarks>
    internal static void AssertUnderRoot(string fullPath, string? workspaceRoot)
    {
        if (string.IsNullOrEmpty(workspaceRoot)) throw new NoWorkspaceRootException();
        // ⚠ Another machine is refused from the path's TEXT, before resolving its links: resolving them is a connection to
        // that host. One place for every built-in reader, whichever argument the path came in.
        if (Execution.NetworkPath.OutsideTheWorkspace(fullPath, workspaceRoot))
            throw new ArgumentException(
                $"Access denied: path is on another machine on the network, outside the workspace root.\n" +
                $"  Requested : {fullPath}\n" +
                $"  Workspace : {workspaceRoot}");

        // Compare link-resolved paths: Path.GetFullPath normalises ./ and ../ but follows no
        // symlink or junction, so a link planted inside the workspace would otherwise let a
        // write escape it while still passing a textual prefix check. A device prefix is taken off, and what follows it
        // normalised: `\\?\C:\ws\..\outside` is outside.
        var rootBare = Trim(ResolveLinks(Path.GetFullPath(workspaceRoot)));
        var target   = Trim(ResolveLinks(OperatingSystem.IsWindows() ? Path.GetFullPath(WithoutDevicePrefix(fullPath)) : fullPath));

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

    /// <summary>
    /// For a WRITE: throws when the file, under another of its names (a hard link), lives outside the workspace — writing
    /// it changes that file too.
    /// </summary>
    /// <remarks>Writes only: a dependency manager links the files of a project's dependencies to its shared store (pnpm's
    /// <c>node_modules</c>, uv's environments), and reading them reads what the project has; writing one changes every
    /// project that shares it. Every write passes <c>FileHistoryService.BackUpBeforeChangeAsync</c>, which asks here.</remarks>
    internal static void AssertEveryNameUnderRoot(string fullPath, string? workspaceRoot)
    {
        if (string.IsNullOrEmpty(workspaceRoot)) return;
        var rootReached = Canonical(Path.GetFullPath(workspaceRoot));
        foreach (var name in OtherNames(fullPath))
        {
            var reached = Canonical(name);
            if (!string.Equals(reached, rootReached, PathComparison)
                && !reached.StartsWith(rootReached + Path.DirectorySeparatorChar, PathComparison))
                throw new ArgumentException(
                    $"Access denied: this file is also {name}, outside the workspace root (a hard link): writing it changes "
                    + $"that file too.\n  Requested : {fullPath}\n  Workspace : {workspaceRoot}");
        }
    }

    /// <summary>
    /// The file a rooted path reaches, as the file system resolves it: a device prefix (<c>\\?\</c>, <c>\\.\</c>) or
    /// this machine's administrative share (<c>\\localhost\C$\</c>) taken back to its drive, a stream or index suffix
    /// taken off each name (<c>.inferpal::$INDEX_ALLOCATION</c> IS the folder), trailing dots and spaces dropped; then,
    /// under Windows, the system's own final path of the deepest part that exists (links, junctions, a <c>subst</c>
    /// drive, a folder-mounted volume, short names, the case on disk), elsewhere the links resolved at every level.
    /// Never throws: a path it cannot read comes back as given.
    /// </summary>
    /// <remarks>⚠ What every approval control judges besides the spelling — the ignore files, the forced card of the
    /// agent's instructions, a rule: each of those spellings reaches the file while its text names another path.</remarks>
    internal static string Canonical(string fullPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return Trim(ResolveLinks(Path.GetFullPath(fullPath)));
            var path = Path.GetFullPath(WithoutStreamSuffixes(WithoutDevicePrefix(fullPath)));
            return Trim(WithoutDevicePrefix(FinalPath(path) ?? ResolveLinks(path)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return Trim(fullPath);
        }
    }

    /// <summary>The system's final path of the deepest existing part of <paramref name="path"/>, the rest appended; null
    /// when nothing can be opened (Windows).</summary>
    private static string? FinalPath(string path)
    {
        var remainder = string.Empty;
        var current = path;
        for (var depth = 0; depth < 64; depth++)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                using var handle = OpenForAttributes(current);
                if (handle.IsInvalid) return null;
                var buffer = new char[32768];
                var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
                if (length == 0 || length >= buffer.Length) return null;
                var head = new string(buffer, 0, (int)length);
                return remainder.Length == 0 ? head : Path.Combine(head, remainder);
            }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) return null;
            remainder = remainder.Length == 0 ? Path.GetFileName(current) : Path.Combine(Path.GetFileName(current), remainder);
            current = parent;
        }
        return null;
    }

    /// <summary>A Windows device path or this machine's administrative share, as the drive path it names; any other path
    /// as given (a volume without a letter, a device namespace).</summary>
    private static string WithoutDevicePrefix(string path)
    {
        foreach (var prefix in (string[])[@"\\?\", @"\\.\", "//?/", "//./"])
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = path[prefix.Length..];
            if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("UNC/", StringComparison.OrdinalIgnoreCase))
                path = @"\\" + rest[4..];   // a share: this machine's administrative one is mapped below
            else if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':')
                return rest;
            else
                return path;
            break;
        }
        if (path.Length > 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/')
        {
            var parts = path[2..].Split(['\\', '/'], 3);
            if (parts.Length >= 2 && IsThisMachine(parts[0])
                && parts[1].Length == 2 && char.IsAsciiLetter(parts[1][0]) && parts[1][1] == '$')
                return parts[1][0] + @":\" + (parts.Length == 3 ? parts[2] : string.Empty);
        }
        return path;
    }

    private static bool IsThisMachine(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1" or "[::1]"
        || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Each name after the root without its stream or index suffix (everything from its first <c>:</c>) and
    /// without trailing dots and spaces — what NTFS opens.</summary>
    private static string WithoutStreamSuffixes(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var names = path[root.Length..].Split(['\\', '/']);
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            var colon = name.IndexOf(':');
            if (colon >= 0) name = name[..colon];
            if (name is not ("." or "..")) name = name.TrimEnd('.', ' ');
            names[i] = name;
        }
        return root + string.Join('\\', names);
    }

    /// <summary>
    /// The other names of the file <paramref name="fullPath"/> names — its hard links —, full paths on the drive that
    /// holds the file; empty when it has one, does not exist, is a folder, lives on a share or a volume without a letter,
    /// or the system does not say (only Windows lists them).
    /// </summary>
    /// <remarks>⚠ The names the system lists are relative to the VOLUME: read through a <c>subst</c> drive or a volume
    /// mounted in a folder, the spelling's drive is not theirs — the drive comes from the file's final path.</remarks>
    internal static IReadOnlyList<string> OtherNames(string fullPath)
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            if (!File.Exists(fullPath)) return [];
            using var handle = OpenForAttributes(fullPath);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks <= 1) return [];
            var buffer = new char[32768];
            var finalLength = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (finalLength == 0 || finalLength >= buffer.Length) return [];
            var final = new string(buffer, 0, (int)finalLength);
            var self = WithoutDevicePrefix(final);
            if (self.Length < 3 || self[1] != ':') return [];
            var volume = self[..2];
            var names = new List<string>();
            var length = (uint)buffer.Length;
            var find = FindFirstFileNameW(final, 0, ref length, buffer);
            if (find == new IntPtr(-1)) return [];
            try
            {
                do
                {
                    var name = volume + new string(buffer, 0, (int)Math.Max(0, length - 1));
                    if (!string.Equals(name, self, PathComparison)) names.Add(name);
                    length = (uint)buffer.Length;
                }
                while (names.Count < 64 && FindNextFileNameW(find, ref length, buffer));
            }
            finally { FindClose(find); }
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or EntryPointNotFoundException or DllNotFoundException)
        {
            Diagnostics.Swallow("PathSanitizer.OtherNames", ex);
            return [];
        }
    }

    /// <summary>A handle that reads nothing but the file's identity — a folder too, a file another process holds open too.</summary>
    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenForAttributes(string path) =>
        CreateFileW(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);

    private const uint FileReadAttributes = 0x80, ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public uint CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out FileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileNameW(string fileName, uint flags, ref uint length, [Out] char[] linkName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileNameW(IntPtr find, ref uint length, [Out] char[] linkName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr find);

    /// <summary>
    /// <see cref="AssertUnderRoot"/>'s answer as a boolean — links and case included; a path that cannot be resolved
    /// counts as outside. For callers that SAY what the tools cannot reach rather than refuse a call.
    /// </summary>
    internal static bool IsUnderRoot(string fullPath, string? workspaceRoot)
    {
        try
        {
            AssertUnderRoot(fullPath, workspaceRoot);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Windows paths are case-insensitive; Linux/macOS ones are not — and the host now
    /// ships for all three (VS Code publishes linux-* and darwin-* builds).</summary>
    // ⚠ Read from the one place that answers this, never re-derived: a copy claiming macOS is
    // case-SENSITIVE makes this guard refuse a perfectly legitimate write whenever the root is
    // spelled with another case.
    private static StringComparison PathComparison => Services.PathComparer.Comparison;

    private static string Trim(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The path with its links resolved at every level, as <see cref="AssertUnderRoot"/> compares it.</summary>
    internal static string Resolved(string fullPath) => Trim(ResolveLinks(fullPath));

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
    /// happily concludes "not a link". On macOS <c>/var</c> is a symlink to <c>/private/var</c>, so
    /// a workspace under <c>/var/folders/...</c> resolves to itself while a path the product derives
    /// from the process's current directory — which the kernel hands back already resolved — comes
    /// out under <c>/private/var/...</c>. The two no longer share a prefix, and
    /// <see cref="AssertUnderRoot"/> refuses a perfectly legitimate write.
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
            // contain: on macOS, where `/var` links to `/private/var`, resolving `<root>/alias`
            // yields `/var/folders/…/real` while the root itself already came out as
            // `/private/var/folders/…`, the two stop sharing a prefix, and a legitimate write is
            // refused. ⚠ Windows cannot see this — there `ResolveLinkTarget(returnFinalTarget:
            // true)` goes through GetFinalPathNameByHandle, which canonicalises the whole path —
            // hence the portable test that records the target THROUGH a second link.
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
