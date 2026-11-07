using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services.Signals;

/// <summary>Payload of a chat-busy marker: which process holds a chat lease, when it was set, and whether it waits.</summary>
/// <param name="Paused">Every turn of the writer waits for a person (its leases let go); <c>null</c> from a writer
/// older than the pause, which never lets its lease go.</param>
internal sealed record ChatBusyState(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("ts")]  long Ts,
    [property: JsonPropertyName("paused")] bool? Paused = null);

/// <summary>
/// File-based IPC channel publishing "a chat/agent request is in flight" from each Inferpal process that answers
/// (where <see cref="GpuScheduler"/> lives) to every other one of the account: the ghost text (FIM) yields the shared
/// GPU to the chat, the other processes' background indexing waits for it, and nothing unloads the model of a turn or
/// starts a <c>/task</c> beside it. Each process writes its own marker and reads the others'.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Deliberately <b>account-wide as a channel</b>: the account's editors share one GPU, so a
/// chat in one editor must silence the ghost-text of every other. But the <em>writing</em> is
/// per-process: each writer owns <c>chat_busy.&lt;pid&gt;.json</c> and
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
/// Migration: unlike the family-A identity
/// channels, this is an account-wide <em>hint</em>, so the legacy unscoped <c>chat_busy.json</c> of
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

    /// <summary>
    /// The unscoped file of a writer older than per-instance signal files: read, never written,
    /// never deleted.
    /// </summary>
    internal static string LegacyFilePath => SignalFile.PathFor($"{BaseName}.json");

    /// <summary>Safety fuse: a busy marker older than this is treated as stale (crash without Clear).</summary>
    internal static TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(10);

    // ── Host side (GpuScheduler) ────────────────────────────────────────────────

    /// <summary>Marks a chat/agent request of this process as in flight: working on the GPU, or
    /// <paramref name="paused"/> while every turn of this process waits for a person. Also called
    /// periodically during long runs to keep the marker under <see cref="MaxAge"/>.</summary>
    internal static void Write(bool paused = false)
    {
        SignalFile.Write(FilePath,
            new ChatBusyState(Environment.ProcessId, SignalFile.Now.ToUnixTimeMilliseconds(), paused),
            "ChatBusySignal.Write");
    }

    /// <summary>Clears <b>this process's</b> busy marker (last chat lease released). Safe if absent.</summary>
    internal static void Clear()
    {
        SignalFile.Delete(FilePath);
    }

    // ── Readers (ghost text, the other processes) ───────────────────────────────

    /// <summary>
    /// True when a chat/agent request works on the GPU <b>in any process of the account</b> (so FIM should
    /// skip): at least one marker — scoped or legacy — whose writer is alive and within
    /// <see cref="MaxAge"/>, and which does not say its turn waits for a person.
    /// </summary>
    internal static bool IsBusy() => Any(skipThisProcess: false, s => s.Paused != true);

    /// <summary>
    /// True when a chat/agent request of ANOTHER process works on the GPU: what a background embedding loop of this
    /// process yields to. This process's own marker is never counted — its leases are known exactly in memory
    /// (<see cref="Hardware.GpuScheduler"/>). ⚠ Only a marker that says it works (<c>paused: false</c>): a writer older
    /// than the pause never lets its lease go at an approval card, and its card left overnight would hold this
    /// process's indexing all night.
    /// </summary>
    internal static bool IsBusyElsewhere() => Any(skipThisProcess: true, s => s.Paused == false);

    /// <summary>
    /// True when a chat/agent request of ANOTHER process is in flight, waiting for a person included: what must not be
    /// cut or doubled under it (its model unloaded, a <c>/task</c> run started beside it).
    /// </summary>
    internal static bool IsTurnInFlightElsewhere() => Any(skipThisProcess: true, _ => true);

    private static bool Any(bool skipThisProcess, Func<ChatBusyState, bool> counts)
    {
        var busy = false;
        foreach (var path in SignalFile.EnumerateKeyedPaths(BaseName))
        {
            var state = SignalFile.TryRead<ChatBusyState>(path);
            if (state is null) continue;   // unreadable = no signal; never delete what we can't judge
            if (skipThisProcess && state.Pid == Environment.ProcessId) continue;

            if (!SignalFile.WriterAlive(state.Pid, state.Ts))
            {
                SignalFile.Delete(path);   // provably dead writer: safe cleanup, markers don't pile up
                continue;
            }

            if (IsFresh(state) && counts(state)) busy = true;
            // Stale-but-alive: silent for FIM, but the file belongs to its (living) writer — leave it.
        }

        // Honour an old-version writer's unscoped marker (read-only — see the migration remark).
        var legacy = SignalFile.TryRead<ChatBusyState>(LegacyFilePath);
        if (legacy is not null && !(skipThisProcess && legacy.Pid == Environment.ProcessId)
            && SignalFile.WriterAlive(legacy.Pid, legacy.Ts) && IsFresh(legacy) && counts(legacy)) busy = true;

        return busy;
    }

    private static bool IsFresh(ChatBusyState state)
    {
        var age = SignalFile.Now - DateTimeOffset.FromUnixTimeMilliseconds(state.Ts);
        return age >= TimeSpan.Zero && age < MaxAge;
    }
}
