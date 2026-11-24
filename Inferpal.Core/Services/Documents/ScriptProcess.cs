using System.Diagnostics;
using System.IO;
using System.Text;

namespace Inferpal.Services.Documents;

/// <summary>
/// How the readers start the programs they run (a converter, a capture, a script of their own): windowless, the arguments as
/// a list, both outputs read as UTF-8 — and on Windows, a script of their own under Windows PowerShell 5.1, the one every
/// Windows has.
/// </summary>
internal static class ScriptProcess
{
    /// <summary>The start of <paramref name="file"/> with <paramref name="args"/>, its outputs redirected.</summary>
    internal static ProcessStartInfo Start(string file, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>
    /// Writes <paramref name="text"/> as <paramref name="name"/> in <paramref name="dir"/> and gives the start that runs it under
    /// Windows PowerShell 5.1, <paramref name="args"/> after it.
    /// </summary>
    internal static ProcessStartInfo WindowsPowerShell(string dir, string name, string text, params string[] args)
    {
        var script = Path.Combine(dir, name);
        // With a byte order mark: Windows PowerShell 5.1 reads a script without one in the ANSI code page.
        File.WriteAllText(script, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return Start(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                     ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, .. args]);
    }
}
