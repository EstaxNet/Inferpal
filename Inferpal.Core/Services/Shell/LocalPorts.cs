using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Inferpal.Services.Shell;

/// <summary>
/// Which processes listen on a TCP port of this machine, and whether they all belong to a process tree — the tree of an
/// app the agent started (<c>interact_page</c> and <c>view_page</c> need no card in such an app's pages only).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Every socket that listens on the port, not one of them: the person's own server may listen on <c>[::1]:5173</c> while
/// the agent's listens on <c>127.0.0.1:5173</c>, and a browser asked for <c>localhost:5173</c> may reach either. A socket
/// whose owner cannot be read (another account's, another namespace's) is not the agent's.
/// </para>
/// <para>
/// Windows: <c>Get-NetTCPConnection</c> and the process list with each one's parent and start; a parent started after its
/// child, or a process started after the socket it is said to own, is another process that got a dead one's id. Linux:
/// <c>/proc</c>, which gives each orphan a new parent. macOS: <c>lsof</c> (the account's own sockets), counted against
/// <c>netstat</c> (all of them), and <c>ps</c>. Whatever cannot be read answers "no": the card asks.
/// </para>
/// </remarks>
internal static class LocalPorts
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>A process: its parent, and when it started where the system says (Windows); <c>null</c> elsewhere.</summary>
    internal readonly record struct ProcessEntry(int Parent, DateTime? Started);

    /// <summary>A socket that listens on the port: its owner (<c>0</c> when it cannot be read) and, where the system says,
    /// when it was created.</summary>
    internal readonly record struct Listener(int Pid, DateTime? Created = null);

    /// <summary>Whether every socket that listens on <paramref name="port"/> belongs to one of the <paramref name="roots"/>'
    /// trees — a root itself or a descendant.</summary>
    internal static async Task<bool> ServedByAsync(int port, IReadOnlyCollection<int> roots, CancellationToken ct)
    {
        if (roots.Count == 0) return false;
        try
        {
            var (listeners, processes) = await SnapshotAsync(port, ct).ConfigureAwait(false);
            return Served(listeners, roots, processes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or InvalidOperationException or FormatException)
        {
            Diagnostics.Swallow("LocalPorts.ServedBy", ex);
            return false;
        }
    }

    /// <summary>Whether there is a listener, and each one belongs to a root's tree.</summary>
    internal static bool Served(IReadOnlyList<Listener> listeners, IReadOnlyCollection<int> roots,
                                IReadOnlyDictionary<int, ProcessEntry> processes) =>
        listeners.Count > 0 && listeners.All(l => l.Pid > 0 && Owns(l, processes) && Descends(l.Pid, roots, processes));

    /// <summary>Whether the process now under the listener's id existed when the socket was created.</summary>
    private static bool Owns(Listener listener, IReadOnlyDictionary<int, ProcessEntry> processes) =>
        listener.Created is not { } created || !processes.TryGetValue(listener.Pid, out var owner)
        || owner.Started is not { } started || started <= created;

    /// <summary>Whether <paramref name="pid"/> is one of the <paramref name="roots"/> or under one, stepping only to a parent
    /// the list holds and that started before its child.</summary>
    internal static bool Descends(int pid, IReadOnlyCollection<int> roots, IReadOnlyDictionary<int, ProcessEntry> processes)
    {
        var current = pid;
        for (var hops = 0; hops < 128 && current > 0; hops++)
        {
            if (roots.Contains(current)) return true;
            if (!processes.TryGetValue(current, out var entry) || entry.Parent == current) return false;
            if (!processes.TryGetValue(entry.Parent, out var parent)) return false;
            // A parent that started after its child is another process that got a dead parent's id.
            if (entry.Started is { } childStarted && parent.Started is { } parentStarted && parentStarted > childStarted) return false;
            current = entry.Parent;
        }
        return false;
    }

    /// <summary>The sockets that listen on <paramref name="port"/>, and every process with its parent; none when the system
    /// could not be read.</summary>
    internal static async Task<(IReadOnlyList<Listener> Listeners, IReadOnlyDictionary<int, ProcessEntry> Processes)> SnapshotAsync(
        int port, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return await WindowsAsync(port, ct).ConfigureAwait(false);
        if (OperatingSystem.IsLinux()) return (LinuxListeners(port), LinuxProcesses());
        return await MacAsync(port, ct).ConfigureAwait(false);
    }

    private const string WindowsScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        Get-NetTCPConnection -State Listen -LocalPort __PORT__ | ForEach-Object {
          $c = if ($_.CreationTime) { $_.CreationTime.ToUniversalTime().Ticks } else { 0 }
          "L $($_.OwningProcess) $c" }
        Get-CimInstance Win32_Process | ForEach-Object {
          $t = if ($_.CreationDate) { $_.CreationDate.ToUniversalTime().Ticks } else { 0 }
          "P $($_.ProcessId) $($_.ParentProcessId) $t" }
        "END"
        """;

    private static async Task<(IReadOnlyList<Listener>, IReadOnlyDictionary<int, ProcessEntry>)> WindowsAsync(int port, CancellationToken ct)
    {
        var script = WindowsScript.Replace("__PORT__", port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var output = await RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], ct)
            .ConfigureAwait(false);
        var listeners = new List<Listener>();
        var processes = new Dictionary<int, ProcessEntry>();
        // A list cut short (the budget ran out, the script stopped) is no list: its missing lines would read as "no parent".
        if (output is null || !output.TrimEnd().EndsWith("END", StringComparison.Ordinal)) return ([], processes);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is ["L", var l, var c] && int.TryParse(l, CultureInfo.InvariantCulture, out var listener)
                && long.TryParse(c, CultureInfo.InvariantCulture, out var created))
                listeners.Add(new Listener(listener, created > 0 ? new DateTime(created, DateTimeKind.Utc) : null));
            else if (parts is ["P", var a, var b, var t] && int.TryParse(a, CultureInfo.InvariantCulture, out var pid)
                     && int.TryParse(b, CultureInfo.InvariantCulture, out var parent) && long.TryParse(t, CultureInfo.InvariantCulture, out var ticks))
                processes[pid] = new ProcessEntry(parent, ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null);
        }
        return (listeners, processes);
    }

    /// <summary>macOS: the account's own listening sockets by <c>lsof</c>, each counted; a socket <c>netstat</c> lists that
    /// <c>lsof</c> cannot see belongs to another account.</summary>
    private static async Task<(IReadOnlyList<Listener>, IReadOnlyDictionary<int, ProcessEntry>)> MacAsync(int port, CancellationToken ct)
    {
        var p = port.ToString(CultureInfo.InvariantCulture);
        var lsof = await RunAsync("lsof", ["-nP", $"-iTCP:{p}", "-sTCP:LISTEN", "-Fpn"], ct, okExitCodes: [0, 1]).ConfigureAwait(false);
        var netstat = await RunAsync("netstat", ["-an", "-p", "tcp"], ct).ConfigureAwait(false);
        var ps = await RunAsync("ps", ["-A", "-o", "pid=", "-o", "ppid="], ct).ConfigureAwait(false);
        var processes = new Dictionary<int, ProcessEntry>();
        if (lsof is null || netstat is null || ps is null) return ([], processes);
        var listeners = new List<Listener>();
        var owner = 0;
        foreach (var line in lsof.Split('\n'))
        {
            if (line.StartsWith('p')) owner = int.TryParse(line.AsSpan(1).Trim(), CultureInfo.InvariantCulture, out var pid) ? pid : 0;
            else if (line.StartsWith('n')) listeners.Add(new Listener(owner));
        }
        var all = netstat.Split('\n').Count(l => l.Contains("LISTEN", StringComparison.Ordinal)
            && l.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 3 } f && f[3].EndsWith("." + p, StringComparison.Ordinal));
        for (var unseen = all - listeners.Count; unseen > 0; unseen--) listeners.Add(new Listener(0));
        foreach (var line in ps.Split('\n'))
            if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var a, var b]
                && int.TryParse(a, CultureInfo.InvariantCulture, out var pid) && int.TryParse(b, CultureInfo.InvariantCulture, out var parent))
                processes[pid] = new ProcessEntry(parent, null);
        return (listeners, processes);
    }

    /// <summary>Linux: the sockets that listen on the port (<c>/proc/net/tcp</c> and <c>tcp6</c>, state <c>0A</c>), each with
    /// the processes that hold it open — none readable when it is another account's.</summary>
    private static IReadOnlyList<Listener> LinuxListeners(int port)
    {
        var inodes = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var hexPort = port.ToString("X4", CultureInfo.InvariantCulture);
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            if (!File.Exists(table)) continue;
            foreach (var line in File.ReadLines(table).Skip(1))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length > 9 && f[3] == "0A" && f[1].EndsWith(":" + hexPort, StringComparison.OrdinalIgnoreCase)) inodes.TryAdd(f[9], []);
            }
        }
        if (inodes.Count == 0) return [];
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), CultureInfo.InvariantCulture, out var pid)) continue;
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(dir, "fd")))
                    if (new FileInfo(fd).LinkTarget is { } target && target.StartsWith("socket:[", StringComparison.Ordinal)
                        && inodes.TryGetValue(target[8..^1], out var owners) && !owners.Contains(pid))
                        owners.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* another account's, or gone */ }
        }
        return [.. inodes.Values.SelectMany(owners => owners.Count == 0 ? [new Listener(0)] : owners.Select(o => new Listener(o)))];
    }

    /// <summary>Linux: every process with its parent, from <c>/proc/&lt;pid&gt;/stat</c> (the name may hold spaces and
    /// parentheses: the fields are read after its last one).</summary>
    private static IReadOnlyDictionary<int, ProcessEntry> LinuxProcesses()
    {
        var processes = new Dictionary<int, ProcessEntry>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), CultureInfo.InvariantCulture, out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 1 && int.TryParse(fields[1], CultureInfo.InvariantCulture, out var parent))
                    processes[pid] = new ProcessEntry(parent, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* gone */ }
        }
        return processes;
    }

    /// <summary>The program's output, or <c>null</c> when it did not finish in time or failed.</summary>
    private static async Task<string?> RunAsync(string file, string[] args, CancellationToken ct, int[]? okExitCodes = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var run = await ChildProcess.RunAsync(psi, Budget, ct).ConfigureAwait(false);
        return run.TimedOut || !(okExitCodes ?? [0]).Contains(run.ExitCode) ? null : run.Stdout;
    }
}
