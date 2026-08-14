using System.Runtime.InteropServices;

namespace Inferpal.Services.VsIntegration;

/// <summary>
/// Resolves this process's <b>parent</b> process id — the devenv that spawned the out-of-process
/// extensibility host, which is the instance key of the family-A signal channels (ROADMAP §22
/// tranche 2, measured by probe 6: the host is a direct child of its devenv, C2, and the PPID is
/// readable without any VS API, C3).
/// </summary>
/// <remarks>
/// .NET exposes no parent-pid API, so this goes through <c>NtQueryInformationProcess</c> with
/// <c>ProcessBasicInformation</c> — documented enough for a best-effort key, and only ever called
/// on our own process handle. The caller treats a failure as "no key declared", which keeps the
/// legacy unscoped signal file names (pre-§22 behaviour) rather than mispairing.
/// </remarks>
internal static class ParentProcess
{
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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref ProcessBasicInformation processInformation, int processInformationLength,
        out int returnLength);

    /// <summary>The parent process id of the current process. Throws on lookup failure.</summary>
    internal static int GetParentProcessId()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var info   = new ProcessBasicInformation();
        var status = NtQueryInformationProcess(
            self.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status != 0)
            throw new InvalidOperationException($"NtQueryInformationProcess failed: 0x{status:X8}");
        return info.InheritedFromUniqueProcessId.ToInt32();
    }
}
