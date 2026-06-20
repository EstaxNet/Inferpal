using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services;

/// <summary>Payload of the chat-busy signal: which process holds a chat lease, and when it was set.</summary>
internal sealed record ChatBusyState(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("ts")]  long Ts);

/// <summary>
/// File-based IPC channel publishing "a chat/agent request is in flight" from the out-of-process
/// extension host (where <see cref="GpuScheduler"/> lives) to the in-process ghost-text in
/// devenv (<see cref="Inferpal.GhostText.GhostTextController"/>), so FIM yields the shared GPU
/// to the chat. This is <see cref="DebuggerStateSignal"/> in reverse: there devenv writes and the
/// host reads; here the host writes and devenv reads.
/// </summary>
/// <remarks>
/// The host writes on the first chat lease and clears on the last. A reader must not trust a stale
/// file left by a crashed host, so <see cref="IsBusy"/> ignores it when the writer process is gone
/// <em>or</em> the timestamp is older than <see cref="MaxAge"/> (a safety fuse: a crash between
/// Write and Clear must not freeze ghost-text forever).
/// </remarks>
internal static class ChatBusySignal
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "Inferpal");

    /// <summary>Full path of the signal file.</summary>
    internal static string FilePath { get; } = Path.Combine(TempDir, "chat_busy.json");

    /// <summary>Safety fuse: a busy signal older than this is treated as stale (crash without Clear).</summary>
    internal static TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(10);

    // Overridable in tests so staleness is decidable without a live process / real clock.
    internal static Func<int, bool>? _isProcessAliveOverride;
    internal static Func<DateTimeOffset>? _nowOverride;

    private static DateTimeOffset Now => _nowOverride?.Invoke() ?? DateTimeOffset.UtcNow;

    // ── Host side (GpuScheduler) ────────────────────────────────────────────────

    /// <summary>Marks the shared GPU as held by a chat/agent request (this process).</summary>
    internal static void Write()
    {
        try
        {
            Directory.CreateDirectory(TempDir);
            var state = new ChatBusyState(System.Diagnostics.Process.GetCurrentProcess().Id,
                                          Now.ToUnixTimeMilliseconds());
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state));
        }
        catch { /* non-critical */ }
    }

    /// <summary>Clears the busy signal (last chat lease released). Safe if absent.</summary>
    internal static void Clear()
    {
        try { File.Delete(FilePath); }
        catch { /* non-critical */ }
    }

    // ── Ghost-text side (devenv) ────────────────────────────────────────────────

    /// <summary>
    /// True when a chat/agent request is currently in flight (so FIM should skip). False when there
    /// is no signal, the writer process is gone, or the signal has exceeded <see cref="MaxAge"/>.
    /// </summary>
    internal static bool IsBusy()
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            var state = JsonSerializer.Deserialize<ChatBusyState>(File.ReadAllText(FilePath));
            if (state is null) return false;
            if (!IsProcessAlive(state.Pid)) return false;

            var age = Now - DateTimeOffset.FromUnixTimeMilliseconds(state.Ts);
            return age >= TimeSpan.Zero && age < MaxAge;
        }
        catch { return false; }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (_isProcessAliveOverride is not null) return _isProcessAliveOverride(pid);
        try { System.Diagnostics.Process.GetProcessById(pid); return true; }
        catch { return false; }
    }
}
