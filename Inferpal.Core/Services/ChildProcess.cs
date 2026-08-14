using System.Diagnostics;

namespace Inferpal.Services;

/// <summary>
/// Starts a redirected child process that can never inherit the parent's standard input.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, and it is not a precaution.</b> A child started with
/// <c>UseShellExecute = false</c> and no <c>RedirectStandardInput</c> inherits the parent's stdin
/// handle. In the VS Code host that handle is the <b>JSON-RPC pipe</b> the editor speaks over —
/// and a console program handed a pipe as stdin can allocate a console and stall on it. Measured on
/// 2026-08-03 by driving the host: <c>git diff --staged</c> never returned, a <c>conhost.exe</c>
/// child appeared next to <c>git.exe</c>, and the process sat at 0 % CPU indefinitely. The identical
/// call takes 31 ms from an ordinary process, which is why nothing in Visual Studio ever showed it:
/// there, stdin is devenv's.
/// </para>
/// <para>
/// Every call site in this code base spawns a child, reads its output and never writes to its
/// input, so redirecting stdin and closing it immediately is both the fix and the correct
/// behaviour: a command that decides to read stdin gets EOF and finishes, instead of blocking a
/// background agent forever on input that is never coming.
/// </para>
/// <para>
/// ⚠ Not for a child the caller talks to (MCP over stdio, the LSP server). Those redirect stdin
/// deliberately and own it; they do not come through here.
/// </para>
/// </remarks>
internal static class ChildProcess
{
    /// <summary>
    /// Configures <paramref name="psi"/> so the child cannot inherit stdin, starts it, and closes
    /// the pipe.
    /// </summary>
    public static Process Start(ProcessStartInfo psi)
    {
        psi.RedirectStandardInput = true;

        var process = Process.Start(psi)!;
        CloseInput(process);
        return process;
    }

    /// <summary>
    /// Same for a caller that needs to build the <see cref="Process"/> itself (event handlers wired
    /// before start): configure, start, then hand it here.
    /// </summary>
    public static void CloseInput(Process process)
    {
        // Best-effort: a child that exited between Start and here has no pipe left, and that is not
        // a failure — the input is closed either way, which is all this guarantees.
        try { process.StandardInput.Close(); }
        catch (Exception ex) { Diagnostics.Swallow("ChildProcess.CloseInput", ex); }
    }
}
