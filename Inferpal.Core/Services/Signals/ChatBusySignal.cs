using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services.Signals;

/// <summary>Payload of a chat-busy marker: which process holds a chat lease, and when it was set.</summary>
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
/// <para>
/// ⚠ Deliberately <b>machine-wide as a channel</b> (ROADMAP §22, family B): there is one GPU, so a
/// chat in one editor must silence the ghost-text of every other. But the <em>writing</em> is
/// per-process (§22 tranche 2, gate G4): each writer owns <c>chat_busy.&lt;pid&gt;.json</c> and
/// <see cref="Clear"/> only ever deletes its own marker. The previous design — one shared file plus
/// an unconditional <c>Clear()</c> — meant that when two hosts chatted in parallel, the first one to
/// finish erased the other's marker and FIM resumed against a busy GPU. Shared read, un-shared
/// write.
/// </para>
/// <para>
/// A reader must not trust a marker left by a crashed host, so <see cref="IsBusy"/> ignores one
/// when the writer process is gone <em>or</em> the timestamp is older than <see cref="MaxAge"/>
/// (a safety fuse: a crash between Write and Clear must not freeze ghost-text forever; the writer
/// keeps a healthy long run under the fuse by refreshing its marker — see
/// <c>GpuScheduler.RefreshBusyMarker</c>). Markers of provably dead writers are deleted
/// opportunistically so they do not pile up; a marker whose writer is still alive is <b>never</b>
/// deleted by anyone but that writer — deleting someone else's live marker is precisely the
/// repaired defect.
/// </para>
/// <para>
/// Migration (§22, family-B nuance from the 2026-08-15 review): unlike the family-A identity
/// channels, this is a machine-wide <em>hint</em>, so the legacy unscoped <c>chat_busy.json</c> of
/// an old-version writer is still <b>read</b> — honouring it keeps FIM quiet during that chat,
/// which is correct. It is never written to and never deleted: an old-version pair on the same
/// machine still owns it.
/// </para>
/// </remarks>
internal static class ChatBusySignal
{
    private const string BaseName = "chat_busy";

    /// <summary>Full path of <b>this process's</b> marker file.</summary>
    internal static string FilePath => SignalFile.KeyedPathFor(BaseName, Environment.ProcessId);

    /// <summary>The unscoped file of a pre-§22 writer: read, never written, never deleted.</summary>
    internal static string LegacyFilePath => SignalFile.PathFor($"{BaseName}.json");

    /// <summary>Safety fuse: a busy marker older than this is treated as stale (crash without Clear).</summary>
    internal static TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(10);

    // ── Host side (GpuScheduler) ────────────────────────────────────────────────

    /// <summary>Marks the shared GPU as held by a chat/agent request (this process). Also called
    /// periodically during long runs to keep the marker under <see cref="MaxAge"/>.</summary>
    internal static void Write()
    {
        SignalFile.Write(FilePath,
            new ChatBusyState(Environment.ProcessId, SignalFile.Now.ToUnixTimeMilliseconds()),
            "ChatBusySignal.Write");
    }

    /// <summary>Clears <b>this process's</b> busy marker (last chat lease released). Safe if absent.</summary>
    internal static void Clear()
    {
        SignalFile.Delete(FilePath);
    }

    // ── Ghost-text side (devenv) ────────────────────────────────────────────────

    /// <summary>
    /// True when a chat/agent request is in flight <b>anywhere on the machine</b> (so FIM should
    /// skip): at least one marker — scoped or legacy — whose writer is alive and within
    /// <see cref="MaxAge"/>.
    /// </summary>
    internal static bool IsBusy()
    {
        var busy = false;
        foreach (var path in SignalFile.EnumerateKeyedPaths(BaseName))
        {
            var state = SignalFile.TryRead<ChatBusyState>(path);
            if (state is null) continue;   // unreadable = no signal; never delete what we can't judge

            if (!SignalFile.IsProcessAlive(state.Pid))
            {
                SignalFile.Delete(path);   // provably dead writer: safe cleanup, markers don't pile up
                continue;
            }

            if (IsFresh(state)) busy = true;
            // Stale-but-alive: silent for FIM, but the file belongs to its (living) writer — leave it.
        }

        // Honour an old-version writer's unscoped marker (read-only — see the migration remark).
        var legacy = SignalFile.TryRead<ChatBusyState>(LegacyFilePath);
        if (legacy is not null && SignalFile.IsProcessAlive(legacy.Pid) && IsFresh(legacy)) busy = true;

        return busy;
    }

    private static bool IsFresh(ChatBusyState state)
    {
        var age = SignalFile.Now - DateTimeOffset.FromUnixTimeMilliseconds(state.Ts);
        return age >= TimeSpan.Zero && age < MaxAge;
    }
}
