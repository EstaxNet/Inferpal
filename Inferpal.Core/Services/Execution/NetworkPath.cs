using System.IO;

namespace Inferpal.Services.Execution;

/// <summary>
/// Whether a path names ANOTHER machine on the network, read from its TEXT alone — never from the disk: asking the file
/// system about <c>\\host\share</c> is already a connection to that host (SMB, ~21 s against one that does not answer).
/// </summary>
/// <remarks>
/// Windows only: elsewhere <c>//x</c> is a local path, and a mounted share (<c>/Volumes</c>, <c>/net/host</c>) looks like any
/// folder. ⚠ This machine's own shares (<c>\\localhost\C$</c>, <c>\\127.0.0.1\c$</c>, its own name) are NOT the network: they
/// reach the workspace's files, and the exclusion rules must keep judging them. ⚠ A local path that LEADS to the network (a
/// link, a junction, a mapped drive letter) is not seen here. A free-text argument that starts with two slashes
/// (<c>//api/users</c>) reads as a host: refused with its cause, which the model can rephrase.
/// </remarks>
internal static class NetworkPath
{
    private static readonly string[] UncPrefixes =
        [@"\\?\UNC\", @"\\.\UNC\", @"\??\UNC\",
         @"\\?\GLOBALROOT\Device\Mup\", @"\\.\GLOBALROOT\Device\Mup\", @"\??\GLOBALROOT\Device\Mup\"];

    /// <summary>Whether <paramref name="path"/> is spelled as a path on another machine.</summary>
    public static bool Is(string? path) => Host(path) is { } host && !IsThisMachine(host);

    /// <summary>
    /// Whether <paramref name="path"/> is on another machine AND outside the workspace: a workspace that itself lives on a
    /// share keeps working, its own files are not "the network".
    /// </summary>
    public static bool OutsideTheWorkspace(string? path, string? root)
    {
        if (!Is(path)) return false;
        if (string.IsNullOrWhiteSpace(root) || !Is(root)) return true;
        var p = Plain(path!);
        var r = Path.TrimEndingDirectorySeparator(Plain(root));
        return !(p.Equals(r, PathComparer.Comparison) || p.StartsWith(r + '\\', PathComparer.Comparison));
    }

    /// <summary>
    /// Where <paramref name="fullPath"/> lands on another machine through the links on its way — the network path, link
    /// targets substituted and the rest of the path kept — or <c>null</c> when it stays on this machine. Each link is read
    /// from its own data and its target walked as text, never through it: following a link to a share is the connection
    /// (SMB, ~21 s against a host that does not answer), a chain of local links included.
    /// </summary>
    /// <remarks>Windows only. A path already spelled on another machine is not walked (<see cref="Is"/> judges it). A mapped
    /// network drive is not walked either (a stat of a dead one waits for its timeout); a junction cannot target a share. Stops
    /// at the first component that does not exist: nothing after it can be a link.</remarks>
    public static string? Landing(string? fullPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathFullyQualified(fullPath.Trim())) return null;
        var path = fullPath.Trim();
        for (var hops = 0; hops < 32; hops++)
        {
            if (Is(path)) return Plain(path);
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
            var volume = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(volume) || volume.StartsWith(@"\\", StringComparison.Ordinal) || IsNetworkDrive(volume)) return null;
            var parts = full[volume.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            var current = volume;
            string? next = null;
            for (var i = 0; i < parts.Length && next is null; i++)
            {
                var parent = current;
                current = Path.Combine(current, parts[i]);
                try
                {
                    // The attributes of the entry itself: a link's are read without following it.
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
                    FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
                    if (entry.LinkTarget is not { } target) continue;
                    // The target, as written (relative to the link's folder), then the rest of the path: walked again.
                    var rest = string.Join('\\', parts.Skip(i + 1));
                    var landed = Path.IsPathFullyQualified(target) || Is(target) ? target : Path.Combine(parent, target);
                    next = rest.Length == 0 ? landed : landed.TrimEnd('\\', '/') + '\\' + rest;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return null;   // a component that does not exist (or cannot be read): nothing after it is reached
                }
            }
            if (next is null) return null;
            path = next;
        }
        return null;   // a cycle of links: nothing lands
    }

    /// <summary>
    /// Whether <paramref name="fullPath"/> leads, through links, to another machine outside the workspace — the workspace's
    /// own links followed the same way: a workspace that is a link to a share keeps its files.
    /// </summary>
    public static bool LeadsOutside(string? fullPath, string? root) =>
        Landing(fullPath) is { } landed && OutsideTheWorkspace(landed, Landing(root) ?? root);

    private static bool IsNetworkDrive(string volume)
    {
        try { return volume.Length >= 2 && volume[1] == ':' && new DriveInfo(volume[..1]).DriveType == DriveType.Network; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The host a UNC spelling names, or <c>null</c> when the path is not a UNC path (or not on Windows).</summary>
    private static string? Host(string? path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return null;
        var s = path.Trim().Replace('/', '\\');
        string rest;
        if (UncPrefixes.FirstOrDefault(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix)
            rest = s[prefix.Length..];
        // A device or long-path prefix on this machine: \\?\C:\…, \\.\C:, \\.\pipe\…
        else if (s.StartsWith(@"\\?\", StringComparison.Ordinal) || s.StartsWith(@"\\.\", StringComparison.Ordinal)
                 || s.StartsWith(@"\??\", StringComparison.Ordinal))
            return null;
        else if (s.Length > 2 && s[0] == '\\' && s[1] == '\\' && s[2] != '\\')
            rest = s[2..];
        else
            return null;
        var end = rest.IndexOf('\\');
        var host = end < 0 ? rest : rest[..end];
        return host.Length == 0 ? null : host;
    }

    private static bool IsThisMachine(string host) =>
        host == "." || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.StartsWith("127.", StringComparison.Ordinal) || host is "::1" or "[::1]" or "0--1.ipv6-literal.net"
        || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The plain <c>\\host\share\…</c> form of a UNC spelling, for comparing a path with a root.</summary>
    private static string Plain(string path)
    {
        var s = path.Trim().Replace('/', '\\');
        foreach (var p in UncPrefixes)
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return @"\\" + s[p.Length..];
        return s;
    }
}
