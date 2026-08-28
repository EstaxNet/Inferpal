using System.Text;

namespace Inferpal.Services.Shell;

/// <summary>
/// Captured result of a foreground persistent-shell command: the user-visible output plus
/// the shell state (cwd + full env snapshot) emitted after the sentinel marker.
/// </summary>
/// <param name="Output">Everything the command printed to stdout, before the state marker.</param>
/// <param name="Cwd">The working directory after the command ran, or <c>null</c> if not captured.</param>
/// <param name="EnvFull">The full environment snapshot after the command ran.</param>
/// <param name="StateCaptured">True if the sentinel marker was found (state lines are trustworthy).</param>
internal readonly record struct ShellRunState(
    string Output,
    string? Cwd,
    IReadOnlyDictionary<string, string> EnvFull,
    bool StateCaptured);

/// <summary>
/// Pure (process-free, testable) protocol for the persistent shell. Inferpal does NOT keep a
/// live REPL pipe — each command spawns a fresh <c>powershell.exe</c>, but a wrapper script
/// restores the saved cwd/env at the start and emits the resulting cwd/env after a unique
/// sentinel marker. This class builds that wrapper and parses the emitted state back out, so
/// stdout/stderr stay cleanly separated (unlike a stdin-driven REPL) and the whole protocol is
/// unit-testable without launching a shell.
/// </summary>
internal static class ShellStateProtocol
{
    /// <summary>Separator between the base64 name and base64 value of an emitted env line.
    /// A vertical bar is never part of the base64 alphabet, so the split is unambiguous.</summary>
    private const char EnvSep = '|';

    /// <summary>Generates a per-call marker so it can never collide with command output.</summary>
    public static string NewMarker() => "INFERPAL_STATE_" + Guid.NewGuid().ToString("N");

    private static string B64Utf8(string s)  => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    private static string B64Utf16(string s) => Convert.ToBase64String(Encoding.Unicode.GetBytes(s));
    private static string FromB64Utf8(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));

    /// <summary>Dialect-dispatching overload (§23): same contract, PowerShell or POSIX wrapper.</summary>
    public static string BuildForegroundScript(ShellDialect dialect, string cwd, IReadOnlyDictionary<string, string> env, string command, string marker) =>
        dialect == ShellDialect.PowerShell
            ? BuildForegroundScript(cwd, env, command, marker)
            : BuildForegroundScriptPosix(cwd, env, command, marker);

    /// <summary>Dialect-dispatching overload (§23): same contract, PowerShell or POSIX wrapper.</summary>
    public static string BuildBackgroundScript(ShellDialect dialect, string cwd, IReadOnlyDictionary<string, string> env, string command) =>
        dialect == ShellDialect.PowerShell
            ? BuildBackgroundScript(cwd, env, command)
            : BuildBackgroundScriptPosix(cwd, env, command);

    /// <summary>
    /// Builds the foreground wrapper script: restore cwd/env, run the (base64-encoded) command in
    /// the current scope via <c>Invoke-Expression</c> so <c>cd</c>/<c>$env:</c>/global state persist,
    /// then emit the marker followed by the resulting cwd and full env. Errors are non-terminating
    /// and the emit runs in a <c>finally</c> so state is captured even when the command fails.
    /// </summary>
    public static string BuildForegroundScript(string cwd, IReadOnlyDictionary<string, string> env, string command, string marker)
    {
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference='Continue'\n");
        AppendRestore(sb, cwd, env);
        sb.Append("try {\n");
        sb.Append("  $__c=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('").Append(B64Utf16(command)).Append("'))\n");
        sb.Append("  Invoke-Expression $__c\n");
        sb.Append("} finally {\n");
        // Terminate any pending console line first ([Console]::Write, native exe without a
        // trailing newline) so the marker always starts a line of its own.
        sb.Append("  Write-Output ''\n");
        sb.Append("  Write-Output '").Append(marker).Append("'\n");
        sb.Append("  Write-Output ('CWD=' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Location).Path)))\n");
        sb.Append("  Get-ChildItem env: | ForEach-Object { 'ENV=' + ([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_.Name))) + '").Append(EnvSep).Append("' + ([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$_.Value))) }\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the background wrapper script: restore cwd/env then run the command. Background jobs
    /// are detached and never write state back, so there is no marker or state emit.
    /// </summary>
    public static string BuildBackgroundScript(string cwd, IReadOnlyDictionary<string, string> env, string command)
    {
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference='Continue'\n");
        AppendRestore(sb, cwd, env);
        sb.Append("$__c=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('").Append(B64Utf16(command)).Append("'))\n");
        sb.Append("Invoke-Expression $__c\n");
        return sb.ToString();
    }

    // ── POSIX dialect (§23) ─────────────────────────────────────────────────────
    // Same design, same injection-proofing: every piece of user data (cwd, env names and values,
    // the command itself) travels as a single-quoted base64 literal — the base64 alphabet contains
    // no shell metacharacter, so an LLM-supplied value can never break out into script. `eval` on
    // the decoded command mirrors Invoke-Expression: cd/export run in the current shell, so they
    // persist into the state emit. There is no try/finally in sh — statements simply run in
    // sequence, and a command that calls `exit` skips the emit, which ParseForeground already
    // treats as "no state captured" (the exact PowerShell caveat).

    private static string BuildForegroundScriptPosix(string cwd, IReadOnlyDictionary<string, string> env, string command, string marker)
    {
        var sb = new StringBuilder();
        AppendRestorePosix(sb, cwd, env);
        sb.Append("__c=$(printf '%s' '").Append(B64Utf8(command)).Append("' | base64 -d)\n");
        sb.Append("eval \"$__c\"\n");
        // Leading \n terminates any unterminated output line (printf/base64 -d without a
        // trailing newline) so the marker always starts a line of its own; a doubled newline
        // on well-behaved output is folded away by the parser's TrimEnd.
        sb.Append("printf '\\n%s\\n' '").Append(marker).Append("'\n");
        // base64 wraps at 76 columns on GNU and BSD alike — `tr -d '\n'` keeps each state line whole.
        sb.Append("printf 'CWD=%s\\n' \"$(printf '%s' \"$PWD\" | base64 | tr -d '\\n')\"\n");
        // awk's ENVIRON iteration is POSIX and, unlike parsing `env` output, immune to values
        // containing '=' or newlines: names come from awk, values from printenv, both re-encoded.
        sb.Append("for __n in $(awk 'BEGIN{for (v in ENVIRON) print v}'); do\n");
        sb.Append("  printf 'ENV=%s").Append(EnvSep).Append("%s\\n' \"$(printf '%s' \"$__n\" | base64 | tr -d '\\n')\" \"$(printf '%s' \"$(printenv \"$__n\")\" | base64 | tr -d '\\n')\"\n");
        sb.Append("done\n");
        return sb.ToString();
    }

    private static string BuildBackgroundScriptPosix(string cwd, IReadOnlyDictionary<string, string> env, string command)
    {
        var sb = new StringBuilder();
        AppendRestorePosix(sb, cwd, env);
        sb.Append("__c=$(printf '%s' '").Append(B64Utf8(command)).Append("' | base64 -d)\n");
        sb.Append("eval \"$__c\"\n");
        return sb.ToString();
    }

    private static void AppendRestorePosix(StringBuilder sb, string cwd, IReadOnlyDictionary<string, string> env)
    {
        sb.Append("__d=$(printf '%s' '").Append(B64Utf8(cwd)).Append("' | base64 -d)\n");
        sb.Append("if [ -d \"$__d\" ]; then cd \"$__d\"; fi\n");
        foreach (var kv in env)
        {
            sb.Append("export \"$(printf '%s' '").Append(B64Utf8(kv.Key))
              .Append("' | base64 -d)\"=\"$(printf '%s' '").Append(B64Utf8(kv.Value))
              .Append("' | base64 -d)\"\n");
        }
    }

    private static void AppendRestore(StringBuilder sb, string cwd, IReadOnlyDictionary<string, string> env)
    {
        sb.Append("$__d=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('").Append(B64Utf8(cwd)).Append("')); if (Test-Path -LiteralPath $__d) { Set-Location -LiteralPath $__d }\n");
        foreach (var kv in env)
        {
            // Name and value are base64-decoded at runtime, so an LLM-supplied env value can never
            // break out of the assignment into arbitrary script.
            sb.Append("Set-Item -LiteralPath ('env:' + [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('")
              .Append(B64Utf8(kv.Key))
              .Append("'))) -Value ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('")
              .Append(B64Utf8(kv.Value))
              .Append("')))\n");
        }
    }

    /// <summary>
    /// Splits captured stdout at the marker into the user-visible output and the trailing state
    /// (cwd + full env). If the marker is absent (e.g. the command called <c>exit</c> and skipped
    /// the <c>finally</c>), the whole text is treated as output and no state is captured.
    /// </summary>
    /// <summary>
    /// Comparer for environment variable NAMES: case-insensitive on Windows, case-sensitive on
    /// POSIX (§27.6 - <c>PATH</c> and <c>path</c> are two distinct variables on Linux; a shared
    /// OrdinalIgnoreCase merged them silently on the published linux-x64/darwin VSIXes).
    /// </summary>
    public static StringComparer EnvNameComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static ShellRunState ParseForeground(string stdout, string marker)
    {
        var lines = (stdout ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var idx = Array.FindIndex(lines, l => l.Trim() == marker);
        // A command whose output has no trailing newline glues the marker to its last line
        // ("hello-gateINFERPAL_STATE_…"): recover the prefix as the real tail of the output.
        // The marker embeds a fresh GUID, so an accidental suffix match cannot happen.
        string? glued = null;
        if (idx < 0)
        {
            idx = Array.FindIndex(lines, l => l.TrimEnd().EndsWith(marker, StringComparison.Ordinal));
            if (idx < 0)
                return new ShellRunState((stdout ?? string.Empty).TrimEnd(), null, EmptyEnv, false);
            var trimmed = lines[idx].TrimEnd();
            glued = trimmed[..^marker.Length];
        }

        var output = string.Join("\n", lines.Take(idx).Concat(glued is null ? [] : [glued])).TrimEnd();
        string? cwd = null;
        var env = new Dictionary<string, string>(EnvNameComparer);
        for (var i = idx + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("CWD=", StringComparison.Ordinal))
            {
                cwd = TryDecode(line[4..]);
            }
            else if (line.StartsWith("ENV=", StringComparison.Ordinal))
            {
                var rest = line[4..];
                var bar = rest.IndexOf(EnvSep);
                if (bar <= 0) continue;
                var name = TryDecode(rest[..bar]);
                var val  = TryDecode(rest[(bar + 1)..]);
                if (name is not null && val is not null) env[name] = val;
            }
        }
        return new ShellRunState(output, cwd, env, true);
    }

    /// <summary>
    /// Diffs a full env snapshot against the baseline (the real process environment) to keep only
    /// the variables the session added or changed. These overrides are what gets re-applied on the
    /// next command, so <c>$env:FOO='x'</c> persists across calls without re-injecting the whole env.
    /// </summary>
    /// <remarks>
    /// Doctrine (§27.6, deliberate): a variable <b>removed</b> by the command
    /// (<c>Remove-Item env:</c>, <c>unset</c>) leaves no trace in the snapshot - it is therefore
    /// not persisted and reappears on the next call, inherited from the process. Same family as
    /// in-memory PowerShell variables and modules: out of scope for the state protocol. Persisting
    /// it would need tombstones in this diff AND in both restore scripts; to be reopened only if a
    /// real use case asks for it.
    /// </remarks>
    public static Dictionary<string, string> ComputeOverrides(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> full)
    {
        var overrides = new Dictionary<string, string>(EnvNameComparer);
        foreach (var kv in full)
        {
            if (!baseline.TryGetValue(kv.Key, out var b) || !string.Equals(b, kv.Value, StringComparison.Ordinal))
                overrides[kv.Key] = kv.Value;
        }
        return overrides;
    }

    private static string? TryDecode(string b64)
    {
        try { return FromB64Utf8(b64.Trim()); }
        catch { return null; }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyEnv =
        new Dictionary<string, string>(0);
}
