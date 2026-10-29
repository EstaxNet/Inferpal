using System.Diagnostics;
using System.IO;

namespace Inferpal.Services;

/// <summary>Opens a page or a file in the application the system gives it — a browser, an editor.</summary>
/// <remarks>
/// ⚠ Off Windows, through the opener program (<c>xdg-open</c>, <c>open</c>) with streams of its own: started by shell
/// execute, it — and the browser it launches — inherits this process's stdin and stdout, which ARE the ACP agent's
/// protocol pipes, and a browser printing "Opening in existing browser session." (or a text browser reading the client's
/// messages) corrupts the protocol. ⚠ Never bound to this process's lifetime: what it opens outlives it.
/// </remarks>
internal static class SystemOpener
{
    /// <summary>Opens <paramref name="target"/>; throws what <see cref="Process.Start(ProcessStartInfo)"/> throws.</summary>
    internal static void Open(string target)
    {
        var start = StartInfoFor(target);
        if (start.UseShellExecute)
        {
            using var _ = Process.Start(start);
            return;
        }
        var opener = Process.Start(start) ?? throw new InvalidOperationException($"{start.FileName} did not start.");
        opener.StandardInput.Close();
        // Drained so a talkative browser never blocks on a full pipe; nothing read there is used.
        _ = opener.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _ = opener.StandardError.BaseStream.CopyToAsync(Stream.Null);
    }

    /// <summary>How <paramref name="target"/> is opened on this system: by shell execute on Windows, by the opener program
    /// with every stream redirected elsewhere.</summary>
    internal static ProcessStartInfo StartInfoFor(string target)
    {
        if (OperatingSystem.IsWindows()) return new ProcessStartInfo(target) { UseShellExecute = true };
        var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
        {
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        start.ArgumentList.Add(target);
        return start;
    }
}
