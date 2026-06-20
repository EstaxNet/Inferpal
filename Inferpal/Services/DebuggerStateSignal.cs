using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services;

/// <summary>One captured stack frame (top-of-stack first).</summary>
internal sealed record DebuggerFrame(
    [property: JsonPropertyName("function")] string Function,
    [property: JsonPropertyName("file")]     string? File,
    [property: JsonPropertyName("line")]     int? Line);

/// <summary>One local variable of the current stack frame.</summary>
internal sealed record DebuggerLocal(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("type")]  string Type,
    [property: JsonPropertyName("value")] string Value);

/// <summary>Snapshot of the debugger at the moment it entered break mode.</summary>
internal sealed record DebuggerSnapshot(
    [property: JsonPropertyName("reason")]    string Reason,
    [property: JsonPropertyName("exception")] string? Exception,
    [property: JsonPropertyName("frames")]    IReadOnlyList<DebuggerFrame> Frames,
    [property: JsonPropertyName("locals")]    IReadOnlyList<DebuggerLocal> Locals,
    [property: JsonPropertyName("pid")]       int Pid,
    [property: JsonPropertyName("ts")]        long Ts);

/// <summary>
/// File-based IPC channel publishing the debugger break state from the in-process
/// <see cref="Inferpal.GhostText.GhostTextPackage"/> (which can use the EnvDTE debugger
/// automation) to the out-of-process extension host (<c>@debugger</c> mention and the
/// <c>get_debugger_state</c> tool), which has no debugger API at all.
/// </summary>
/// <remarks>
/// Protocol mirrors <see cref="ActiveSolutionSignal"/>: the in-process tracker writes on
/// every break, clears when the debugger runs or stops. The state is persistent while VS
/// stays paused (a break can last hours), so readers cannot expire it by age — instead the
/// snapshot records the devenv process id, and a snapshot whose process is gone is treated
/// as stale (left behind by a crashed session).
/// </remarks>
internal static class DebuggerStateSignal
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "Inferpal");

    /// <summary>Full path of the signal file.</summary>
    internal static string FilePath { get; } = Path.Combine(TempDir, "debugger_state.json");

    // Overridden in tests so staleness is decidable without a live process.
    internal static Func<int, bool>? _isProcessAliveOverride;

    // ── In-process side (VsDebuggerTracker) ────────────────────────────────────

    internal static void Write(DebuggerSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(TempDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot));
        }
        catch { /* non-critical */ }
    }

    /// <summary>Deletes the signal (debugger resumed or stopped). Safe if absent.</summary>
    internal static void Clear()
    {
        try { File.Delete(FilePath); }
        catch { /* non-critical */ }
    }

    // ── Out-of-process side (mention / tool) ───────────────────────────────────

    /// <summary>
    /// The current break snapshot, or <c>null</c> when the debugger is not paused (no file,
    /// unreadable file, or a stale file whose VS process no longer exists).
    /// </summary>
    internal static DebuggerSnapshot? TryRead()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var snap = JsonSerializer.Deserialize<DebuggerSnapshot>(File.ReadAllText(FilePath));
            if (snap is null) return null;
            return IsProcessAlive(snap.Pid) ? snap : null;
        }
        catch { return null; }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (_isProcessAliveOverride is not null) return _isProcessAliveOverride(pid);
        try { System.Diagnostics.Process.GetProcessById(pid); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Renders a snapshot as the markdown context block shared by the <c>@debugger</c>
    /// attachment and the <c>get_debugger_state</c> tool result (model-facing, English).
    /// </summary>
    internal static string Format(DebuggerSnapshot snap)
    {
        var sb = new StringBuilder("## Debugger state (paused)\n");
        sb.Append("Break reason: ").Append(snap.Reason).Append('\n');

        if (!string.IsNullOrEmpty(snap.Exception))
            sb.Append("\n### Exception\n").Append(snap.Exception).Append('\n');

        if (snap.Frames.Count > 0)
        {
            sb.Append("\n### Call stack (top first)\n");
            foreach (var f in snap.Frames)
            {
                sb.Append("- ").Append(f.Function);
                if (f.File is not null)
                {
                    sb.Append("  (").Append(f.File);
                    if (f.Line is not null) sb.Append(':').Append(f.Line);
                    sb.Append(')');
                }
                sb.Append('\n');
            }
        }

        if (snap.Locals.Count > 0)
        {
            sb.Append("\n### Locals (current frame)\n");
            foreach (var l in snap.Locals)
                sb.Append("- `").Append(l.Name).Append("` (").Append(l.Type).Append(") = ")
                  .Append(l.Value).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }
}
