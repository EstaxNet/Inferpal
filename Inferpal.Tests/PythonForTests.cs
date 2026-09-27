using System.Diagnostics;
using System.IO;

namespace Inferpal.Tests;

/// <summary>
/// The machine's Python, for the tests that need a real interpreter — never waited on without a budget.
/// </summary>
/// <remarks>
/// ⚠ A <c>python</c> that never answers is a real state of a developer machine: the Windows alias of the Python
/// install manager can hang with no output. An unbounded wait on it holds the whole test run open, and nothing
/// names the test that waits. A call that does not finish in its budget is killed with its tree, and the test
/// that asked is UNDECIDED — the same answer as "no Python on this machine", never a pass.
/// </remarks>
internal static class PythonForTests
{
    private static string Command => OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>Whether <c>python --version</c> answers, within 30 s.</summary>
    internal static Task<bool> IsInstalledAsync() => RunAsync("--version", TimeSpan.FromSeconds(30));

    /// <summary>Makes a virtual environment without pip at <paramref name="dir"/>; <c>false</c> when this Python
    /// cannot (Debian without python3-venv) or does not answer within 60 s.</summary>
    internal static Task<bool> MakeVenvAsync(string dir) =>
        RunAsync($"-m venv --without-pip \"{dir}\"", TimeSpan.FromSeconds(60));

    private static async Task<bool> RunAsync(string arguments, TimeSpan budget)
    {
        Process process;
        try
        {
            // Output inherited, not redirected: an unread pipe fills, and a read one is held open by any child the
            // interpreter leaves behind.
            process = Process.Start(new ProcessStartInfo(Command, arguments) { UseShellExecute = false })!;
        }
        catch { return false; }   // no Python on the PATH

        using (process)
        {
            using var deadline = new CancellationTokenSource(budget);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
        }
    }
}
