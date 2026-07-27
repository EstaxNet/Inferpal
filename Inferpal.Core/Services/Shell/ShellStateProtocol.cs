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
    public static ShellRunState ParseForeground(string stdout, string marker)
    {
        var lines = (stdout ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var idx = Array.FindIndex(lines, l => l.Trim() == marker);
        if (idx < 0)
            return new ShellRunState((stdout ?? string.Empty).TrimEnd(), null, EmptyEnv, false);

        var output = string.Join("\n", lines.Take(idx)).TrimEnd();
        string? cwd = null;
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
    public static Dictionary<string, string> ComputeOverrides(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> full)
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
