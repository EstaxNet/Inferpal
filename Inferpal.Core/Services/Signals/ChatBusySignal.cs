using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services.Signals;

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
    /// <summary>Full path of the signal file.</summary>
    /// <remarks>⚠ Machine-wide on purpose (ROADMAP §22, family B): there is one GPU, so a chat in
    /// one editor must silence the ghost-text of another.</remarks>
    internal static string FilePath => SignalFile.PathFor("chat_busy.json");

    /// <summary>Safety fuse: a busy signal older than this is treated as stale (crash without Clear).</summary>
    internal static TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(10);

    // ── Host side (GpuScheduler) ────────────────────────────────────────────────

    /// <summary>Marks the shared GPU as held by a chat/agent request (this process).</summary>
    internal static void Write()
    {
        SignalFile.Write(FilePath,
            new ChatBusyState(Environment.ProcessId, SignalFile.Now.ToUnixTimeMilliseconds()),
            "ChatBusySignal.Write");
    }

    /// <summary>Clears the busy signal (last chat lease released). Safe if absent.</summary>
    internal static void Clear()
    {
        SignalFile.Delete(FilePath);
    }

    // ── Ghost-text side (devenv) ────────────────────────────────────────────────

    /// <summary>
    /// True when a chat/agent request is currently in flight (so FIM should skip). False when there
    /// is no signal, the writer process is gone, or the signal has exceeded <see cref="MaxAge"/>.
    /// </summary>
    internal static bool IsBusy()
    {
        var state = SignalFile.TryRead<ChatBusyState>(FilePath);
        if (state is null || !SignalFile.IsProcessAlive(state.Pid)) return false;

        var age = SignalFile.Now - DateTimeOffset.FromUnixTimeMilliseconds(state.Ts);
        return age >= TimeSpan.Zero && age < MaxAge;
    }
}
