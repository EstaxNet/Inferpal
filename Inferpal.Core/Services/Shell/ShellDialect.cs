using System.Diagnostics;
using System.IO;

namespace Inferpal.Services.Shell;

/// <summary>Which script language the persistent shell speaks (ROADMAP §23).</summary>
internal enum ShellDialect
{
    /// <summary>Windows PowerShell or pwsh — the original dialect.</summary>
    PowerShell,
    /// <summary>POSIX sh/bash — Linux and macOS hosts.</summary>
    Posix,
}

/// <summary>
/// Resolves which shell this machine runs commands with, and how to hand it a script.
/// </summary>
/// <remarks>
/// Resolution order (§23): Windows → <c>powershell.exe</c>, exactly as before; elsewhere →
/// <c>pwsh</c> when present on PATH (full PowerShell semantics, nothing else changes), otherwise
/// <c>/bin/bash</c> with the POSIX dialect of <see cref="ShellStateProtocol"/>. Resolved per call
/// rather than cached: it costs a PATH scan only off-Windows, and a cache would be one more
/// process-wide static for tests to reset.
/// </remarks>
/// <summary>Immutable dialect + executable pair for the test seam — a reference type on purpose:
/// reference reads/writes are atomic, where the nullable struct tuple it replaces could be read
/// torn by a concurrently-running test class. A torn read materialized once on the ubuntu CI leg
/// (2026-08-20): HasValue already true, Dialect still default(PowerShell = 0), FileName already
/// "/bin/bash" — a PowerShell wrapper handed to bash.</summary>
internal sealed record ShellOverride(ShellDialect Dialect, string FileName);

internal static class ShellLauncher
{
    /// <summary>Test seam: forces a dialect + executable (e.g. Git bash on a Windows dev box).</summary>
    internal static volatile ShellOverride? _overrideForTests;

    /// <summary>The dialect and executable this machine's persistent shell uses.</summary>
    public static (ShellDialect Dialect, string FileName) Resolve()
    {
        if (_overrideForTests is { } o) return (o.Dialect, o.FileName);
        if (OperatingSystem.IsWindows()) return (ShellDialect.PowerShell, "powershell.exe");

        var pwsh = FindOnPath("pwsh");
        return pwsh is not null ? (ShellDialect.PowerShell, pwsh)
                                : (ShellDialect.Posix, "/bin/bash");
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> that runs <paramref name="script"/> under the
    /// given dialect. PowerShell takes the script UTF-16-base64-encoded (<c>-EncodedCommand</c>,
    /// immune to quoting); the POSIX side passes it as a single <c>-c</c> argument via
    /// <see cref="ProcessStartInfo.ArgumentList"/>, which never goes through a shell quoting layer.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(ShellDialect dialect, string fileName, string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        if (dialect == ShellDialect.PowerShell)
        {
            psi.Arguments = $"-NoProfile -NonInteractive -EncodedCommand {ShellSession.Encode(script)}";
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
        }
        return psi;
    }

    private static string? FindOnPath(string name)
    {
        try
        {
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
            foreach (var dir in paths)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* a malformed PATH entry must not break shell resolution */ }
        return null;
    }
}
