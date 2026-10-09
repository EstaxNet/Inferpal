using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Inferpal.Services;

/// <summary>
/// Ties every child process Inferpal starts to the life of the process that started it: a host that dies — killed,
/// crashed, its editor gone — takes its MCP servers, shells, background jobs and their own children with it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ An orderly close already stops them (<c>ToolRegistry.Dispose</c>, the MCP and LSP clients). This is for the end
/// that runs no code at all: without it, a crash leaves a background job, its shell and an MCP server's own child
/// running with no owner, and nothing in either editor can find them again.
/// </para>
/// <para>
/// <b>Windows</b> — one job object per process, <c>KILL_ON_JOB_CLOSE</c>, every child assigned to it as soon as it
/// has started (<see cref="Bind"/>). Only this process holds the handle, and it is not inheritable: when the process
/// ends, by any means, the handle closes and Windows kills everything in the job — a child's own children included,
/// since they are born in its job. <c>BREAKAWAY_OK</c>: a program that explicitly asks to leave
/// (<c>CREATE_BREAKAWAY_FROM_JOB</c>) is let go instead of failing to start.
/// ⚠ The assignment follows the start (<see cref="Process.Start()"/> cannot create a suspended process): a child that
/// started one of its own within those microseconds would leave it outside. No shell, interpreter or runtime starts
/// a child before its own initialisation, which is far longer.
/// ⚠ The process itself is never put in the job: the Visual Studio out-of-process host is not ours alone, and the
/// browser a sign-in opens by shell execute must outlive us.
/// </para>
/// <para>
/// <b>Linux and macOS</b> — no job object, nothing per child. The VS Code host makes itself the leader of a process
/// group of its own at startup (<see cref="LeadOwnProcessGroup"/>), so every descendant is born in that group, and a
/// watchdog in the group kills all of it when the host's end of its pipe closes — the host's death, whatever the
/// cause. A process that leaves the group on purpose (<c>setsid</c>, a daemon) escapes, as it means to.
/// </para>
/// </remarks>
internal static class ProcessLifetime
{
    // ── Windows ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The job every child of this process joins. ⚠ Never disposed: closing the handle IS the kill, and it is meant to
    /// happen when the process ends. Rooted by this static, so the finalizer cannot close it early either.
    /// </summary>
    private static readonly Lazy<SafeFileHandle?> s_job = new(() => CreateKillOnCloseJob(out var failure) ?? Unbound(failure));

    /// <summary>
    /// Assigns <paramref name="child"/>, just started, to this process's job: it — and what it starts afterwards —
    /// dies with this process. Nothing on other systems, where the group does it (see the remarks of the class).
    /// </summary>
    /// <remarks>Best-effort: a child that could not be bound still runs; the failure is said once in /diagnostics.</remarks>
    public static void Bind(Process child)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (s_job.Value is { } job) Bind(child, job);
    }

    /// <summary><see cref="Bind(Process)"/> with the job given — the seam the tests close by hand.</summary>
    internal static bool Bind(Process child, SafeHandle job)
    {
        try
        {
            if (AssignProcessToJobObject(job, child.SafeHandle)) return true;
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            if (!child.HasExited)   // one that already exited has nothing left to outlive us
                Diagnostics.RecordOnce("ProcessLifetime.Bind",
                    $"A child process could not be tied to Inferpal's lifetime ({error.Message}): it would survive a crash.",
                    error.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return false;
        }
        catch (InvalidOperationException) { return false; }   // never started, or already reaped
        catch (Exception ex)
        {
            Diagnostics.Swallow("ProcessLifetime.Bind", ex);
            return false;
        }
    }

    /// <summary>
    /// A new job object that kills every process in it when its last handle closes; <c>null</c> when Windows refused
    /// (<paramref name="failure"/> says why).
    /// </summary>
    internal static SafeFileHandle? CreateKillOnCloseJob(out string? failure)
    {
        failure = null;
        if (!OperatingSystem.IsWindows()) { failure = "not Windows"; return null; }

        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            job.Dispose();
            return null;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            job.Dispose();
            return null;
        }
        return job;
    }

    /// <summary>Whether <paramref name="process"/> runs inside <paramref name="job"/> — what the tests read.</summary>
    internal static bool IsInJob(Process process, SafeHandle job) =>
        IsProcessInJob(process.SafeHandle, job, out var inJob) && inJob;

    /// <summary>The job this process binds its children to (Windows; <c>null</c> elsewhere or when refused).</summary>
    internal static SafeHandle? Job => OperatingSystem.IsWindows() ? s_job.Value : null;

    private static SafeFileHandle? Unbound(string? failure)
    {
        Diagnostics.Record("ProcessLifetime",
            $"No job object for the child processes ({failure}): they would survive a crash of this process.");
        return null;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK      = 0x00000800;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeHandle hJob, int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeHandle hJob, SafeHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeHandle hProcess, SafeHandle hJob, [MarshalAs(UnmanagedType.Bool)] out bool result);

    // ── Linux and macOS ─────────────────────────────────────────────────────

    /// <summary>
    /// The watchdog's process, whose standard input is the pipe whose end is this process's death.
    /// ⚠ Rooted for the life of the process: collected, its pipe would close — and the watchdog would kill this
    /// process's whole group, this process included, while it is running.
    /// </summary>
    private static Process? s_watchdog;

    /// <summary>
    /// Makes this process the leader of a process group of its own and arms the watchdog that kills the group when this
    /// process ends. For a process that owns its children outright — the VS Code host; nothing on Windows, where
    /// <see cref="Bind"/> does it child by child. Call it before the first child starts.
    /// </summary>
    /// <remarks>
    /// ⚠ The watchdog is armed ONLY once the group is verified to be this process's own (<c>getpgrp() == pid</c>): the
    /// group the host is born in is its editor's, and killing that one would take the editor's extension host down.
    /// </remarks>
    public static void LeadOwnProcessGroup()
    {
        if (OperatingSystem.IsWindows() || s_watchdog is not null) return;
        try
        {
            var pid = Environment.ProcessId;
            if (getpgrp() != pid && setpgid(0, 0) != 0)
            {
                Diagnostics.Record("ProcessLifetime",
                    $"Could not lead a process group of its own (errno {Marshal.GetLastPInvokeError()}): the child processes would survive a crash of this process.");
                return;
            }
            if (getpgrp() != pid) return;   // not ours alone: never armed against someone else's group

            s_watchdog = StartGroupWatchdog(pid);
        }
        catch (Exception ex) { Diagnostics.Swallow("ProcessLifetime.LeadOwnProcessGroup", ex); }
    }

    /// <summary>
    /// Starts a shell that waits for the end of its standard input, then kills process group <paramref name="group"/>.
    /// The caller keeps the returned process — its standard input open — for as long as the group must live.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>kill -9 -N</c>, never <c>kill -- -N</c> (dash reads <c>--</c> as a pid and kills nothing) nor <c>kill 0</c>
    /// (whatever group the watchdog happens to be in). A misread group number fails safe: nothing is killed.
    /// ⚠ Every stream redirected: inherited, its output would land in the host's JSON-RPC stream.
    /// </remarks>
    internal static Process StartGroupWatchdog(int group)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("read -r _; kill -9 -" + group.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(psi)!;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setpgid(int pid, int pgid);

    [DllImport("libc")]
    private static extern int getpgrp();
}
