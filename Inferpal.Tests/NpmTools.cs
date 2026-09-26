using System.Diagnostics;

namespace Inferpal.Tests;

/// <summary>Whether Node's npm is installed on the machine running the suite — the tests that need it are UNDECIDED
/// without it, never green.</summary>
internal static class NpmTools
{
    public static bool Installed()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
                OperatingSystem.IsWindows() ? "/c npm --version" : "-c \"npm --version\"")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
