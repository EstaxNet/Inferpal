using System.Diagnostics;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Child processes must never inherit the parent's stdin (roadmap: defect found 2026-08-03 by
// driving the VS Code host — git hung forever on the JSON-RPC pipe it had been handed as stdin).
public class ChildProcessTests
{
    private static ProcessStartInfo Psi(string args) =>
        new("cmd.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

    [Fact]
    public void StdinIsRedirectedAndClosed()
    {
        // The OS-level hang cannot be reproduced in-process: it needs a pipe as the *parent's*
        // stdin, which a test host does not have. What is pinned here is the decision that was
        // missing — the flag — and that the pipe is shut rather than left open.
        using var proc = ChildProcess.Start(Psi("/c exit 0"));

        Assert.True(proc.StartInfo.RedirectStandardInput);
        Assert.Throws<ObjectDisposedException>(() => proc.StandardInput.Write('x'));
    }

    [Fact]
    public async Task AChildThatReadsStdinGetsEofInsteadOfBlockingForever()
    {
        // The behavioural half, and the reason closing beats merely redirecting: a background agent
        // must not be stuck on input nobody will ever type.
        using var proc = ChildProcess.Start(Psi("/c set /p X= & exit 0"));

        var wait  = proc.WaitForExitAsync();
        var first = await Task.WhenAny(wait, Task.Delay(10_000));

        Assert.Same(wait, first);   // the child finished; it did not sit waiting on input
        Assert.True(proc.HasExited);
    }
}
