using System.Runtime.InteropServices;

namespace Inferpal.Services.VsIntegration;

/// <summary>
/// Resolves the devenv that owns this out-of-process extensibility host — the instance key of the family-A signal
/// channels. The PIDs are read without any VS API.
/// </summary>
/// <remarks>
/// ⚠ The host is NOT always a direct child of its devenv: Visual Studio 2026 starts it as a direct child, Visual Studio 2022 starts it
/// through <c>Microsoft.ServiceHub.Controller.exe</c> (devenv → controller → host). Keyed on the direct parent, the
/// host reads the controller's channels while the in-process package writes devenv's: the solution, hence the
/// workspace root, the debugger state and the build banner are lost without a word. Hence the walk up the ancestors,
/// bounded by <see cref="MaxAncestorDepth"/>.
/// </remarks>
/// <remarks>
/// .NET exposes no parent-pid API, so this goes through <c>NtQueryInformationProcess</c> with
/// <c>ProcessBasicInformation</c> — documented enough for a best-effort key, on a handle opened with query rights
/// only. The caller treats a failure as "no key declared", which keeps the legacy unscoped signal file names rather
/// than mispairing.
/// </remarks>
internal static class ParentProcess
{
    /// <summary>How many generations above this process a devenv is looked for: its parent, then the parent's
    /// parent… Two are known (direct child, or child of the ServiceHub controller); a little room above.</summary>
    internal const int MaxAncestorDepth = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref ProcessBasicInformation processInformation, int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>The parent process id of <paramref name="pid"/>. Throws on lookup failure.</summary>
    internal static int GetParentProcessId(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"OpenProcess({pid}) failed: error {Marshal.GetLastWin32Error()}");
        try
        {
            var info   = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(
                handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (status != 0)
                throw new InvalidOperationException($"NtQueryInformationProcess({pid}) failed: 0x{status:X8}");
            return info.InheritedFromUniqueProcessId.ToInt32();
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>The devenv this process belongs to, or <c>null</c> — with what was seen on the way, for the trace.</summary>
    internal static (int? Pid, string Seen) FindOwningDevenv()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return NearestDevenv(self.Id, Describe);
    }

    private static (string Name, int ParentId)? Describe(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return (p.ProcessName, GetParentProcessId(pid));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The nearest ancestor of <paramref name="pid"/> named devenv, at most <see cref="MaxAncestorDepth"/> generations
    /// up; <c>null</c> when none is found, a process cannot be read, or the chain loops. <paramref name="describe"/>
    /// gives a process's name and parent id, <c>null</c> when it cannot be read.
    /// </summary>
    internal static (int? Pid, string Seen) NearestDevenv(int pid, Func<int, (string Name, int ParentId)?> describe)
    {
        var seen    = new List<string>();
        var current = pid;
        for (var depth = 0; depth < MaxAncestorDepth; depth++)
        {
            var parentId = describe(current)?.ParentId ?? 0;
            if (parentId <= 0 || parentId == current) break;
            var parent = describe(parentId);
            if (parent is null) { seen.Add($"pid {parentId} (unreadable)"); break; }
            seen.Add($"{parent.Value.Name} (pid {parentId})");
            if (parent.Value.Name.Equals("devenv", StringComparison.OrdinalIgnoreCase))
                return (parentId, string.Join(" <- ", seen));
            current = parentId;
        }
        return (null, seen.Count == 0 ? "no readable parent" : string.Join(" <- ", seen));
    }
}
