using System.Diagnostics;
using System.IO;
using System.Text;

namespace Inferpal.Services.Shell;

/// <summary>Which script language the persistent shell speaks.</summary>
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
/// Resolution order: Windows → <c>powershell.exe</c>, exactly as before; elsewhere →
/// <c>pwsh</c> when present on PATH (full PowerShell semantics, nothing else changes), otherwise
/// <c>/bin/bash</c> with the POSIX dialect of <see cref="ShellStateProtocol"/>. Resolved per call
/// rather than cached: it costs a PATH scan only off-Windows, and a cache would be one more
/// process-wide static for tests to reset.
/// </remarks>
/// <summary>Immutable dialect + executable pair for the test seam — a reference type on purpose:
/// reference reads/writes are atomic, where the nullable struct tuple it replaces could be read
/// torn by a concurrently-running test class. A torn read materialized once on the ubuntu CI leg
///: HasValue already true, Dialect still default(PowerShell = 0), FileName already
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

        return ResolvePosixHost(FindOnPath, File.Exists);
    }

    /// <summary>The off-Windows half of <see cref="Resolve"/>, with its two probes injected.</summary>
    /// <remarks>
    /// ⚠ <c>pwsh</c> was looked up on PATH while bash was written down as the absolute path
    /// <c>/bin/bash</c> — the same question asked two ways. A host whose bash lives elsewhere
    /// (NixOS puts it in <c>/nix/store</c> and ships only <c>/bin/sh</c>) or that has none at all
    /// (Alpine/busybox, a mainstream dev-container base) was handed an executable that does not
    /// exist, so every <c>run_command</c>, the persistent shell, every background job and every user
    /// shell tool died on a <c>Win32Exception</c> — while the POSIX wrapper of
    /// <see cref="ShellStateProtocol"/> uses nothing but printf/eval/base64/awk/printenv/tr and runs
    /// unchanged under <c>/bin/sh</c>. The last resort is <c>/bin/sh</c> rather than a bash already
    /// known to be absent: POSIX requires that path to exist.
    /// </remarks>
    internal static (ShellDialect Dialect, string FileName) ResolvePosixHost(
        Func<string, string?> onPath, Func<string, bool> exists)
    {
        if (onPath("pwsh") is { } pwsh) return (ShellDialect.PowerShell, pwsh);
        if (onPath("bash") is { } bash) return (ShellDialect.Posix, bash);
        if (exists("/bin/bash"))        return (ShellDialect.Posix, "/bin/bash");
        if (onPath("sh")   is { } sh)   return (ShellDialect.Posix, sh);
        return (ShellDialect.Posix, "/bin/sh");
    }

    /// <summary>How the resolved shell is NAMED to the model — the single reader of that name.</summary>
    /// <remarks>
    /// ⚠ PowerShell keeps its LANGUAGE name (<c>powershell.exe</c> and <c>pwsh</c> speak the same
    /// one); the POSIX side takes the executable's, extension stripped so a Windows box driven at
    /// Git bash reads <c>bash</c> and not <c>bash.exe</c>. bash and sh are <i>not</i> the same
    /// language: a model told "bash" on a busybox host writes <c>[[ ]]</c>, arrays, <c>source</c>
    /// and <c>&lt;&lt;&lt;</c>, and ash refuses them one by one — the very defect §23 repaired in
    /// the other direction, left alive on the leg nobody looked at. A blank name falls back to the
    /// dialect's floor rather than to bash, which is precisely what the host may not have.
    /// </remarks>
    public static string SpokenName(ShellDialect dialect, string fileName) =>
        dialect == ShellDialect.PowerShell           ? "PowerShell"
      : Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } name ? name
      : "sh";

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
            // What the child writes is UTF-8 end to end (see Utf8Console): decoded as the host's console code page
            // otherwise, which a process without a console does not even have.
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding  = Utf8,
        };

        if (dialect == ShellDialect.PowerShell)
        {
            psi.Arguments = $"-NoProfile -NonInteractive -EncodedCommand {ShellSession.Encode(Utf8Console + script)}";
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
        }
        return psi;
    }

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Puts the child's console in UTF-8 before the command runs.
    /// </summary>
    /// <remarks>
    /// ⚠ Windows PowerShell reads and writes a console in its OEM code page (850 on a French machine), while git
    /// (commit messages, the lines of a diff) and dotnet's localized messages write UTF-8: "entières" came back
    /// "enti├¿res", "exécution" "ex├®cution" — and a mangled line from <c>git diff</c> is what the model then quotes
    /// into an edit. With the console in UTF-8, PowerShell decodes a native tool's output as UTF-8, cmd's own output
    /// follows the console, and PowerShell writes UTF-8 to our pipe. <c>$OutputEncoding</c> is what PowerShell pipes
    /// INTO a native tool. Best-effort: a host without a console keeps its defaults.
    /// </remarks>
    private const string Utf8Console =
        "try { $__u = New-Object System.Text.UTF8Encoding $false; [Console]::OutputEncoding = $__u; "
      + "[Console]::InputEncoding = $__u; $OutputEncoding = $__u } catch { }\n";

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
