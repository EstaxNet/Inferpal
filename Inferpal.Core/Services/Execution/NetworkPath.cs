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
