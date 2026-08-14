using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services.Signals;

/// <summary>A pending inline-diff preview: the rewrite of <see cref="FilePath"/> awaiting per-hunk
/// review in the editor. <see cref="OldText"/> must still match the buffer when picked up.</summary>
internal sealed record InlineDiffRequest(
    [property: JsonPropertyName("id")]   string Id,
    [property: JsonPropertyName("pid")]  int    Pid,
    [property: JsonPropertyName("ts")]   long   Ts,
    [property: JsonPropertyName("file")] string FilePath,
    [property: JsonPropertyName("old")]  string OldText,
    [property: JsonPropertyName("new")]  string NewText);

/// <summary>Pickup receipt written by the renderer when it starts showing a request.</summary>
internal sealed record InlineDiffAck(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ts")] long   Ts);

/// <summary>
/// File-based IPC channel carrying an inline-diff preview request from the out-of-process
/// extension host (where code actions run) to the in-process adornment renderer in devenv
/// (<c>Inferpal.GhostText.InlineDiffController</c>) — same transport family as
/// <see cref="ChatBusySignal"/>, with an acknowledgment leg so the host can fall back to a
/// direct apply when no renderer picks the request up (view closed, MEF component missing).
/// The decision itself (accept/reject per hunk) never crosses back: the renderer applies
/// accepted hunks straight to the buffer, where native undo covers them.
/// </summary>
internal static class InlineDiffPreviewSignal
{
    // Scoped to the declared VS instance (§22 tranche 2): a preview requested in one devenv
    // must never be rendered — and consumed — by another.
    internal static string RequestPath => SignalFile.ScopedPathFor("inline_diff_request");
    internal static string AckPath     => SignalFile.ScopedPathFor("inline_diff_ack");

    /// <summary>A request older than this is ignored (host crashed between write and discard).</summary>
    internal static TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(2);

    // ── Host side (code actions) ────────────────────────────────────────────────

    /// <summary>Publishes a preview request and returns its id. Best-effort: on I/O failure the
    /// pickup wait simply times out and the caller falls back to a direct apply.</summary>
    internal static string WriteRequest(string filePath, string oldText, string newText)
    {
        var id = Guid.NewGuid().ToString("N");
        SignalFile.Delete(AckPath);   // a stale ack must not satisfy the new request's wait
        SignalFile.Write(RequestPath,
            new InlineDiffRequest(id, Environment.ProcessId,
                                  SignalFile.Now.ToUnixTimeMilliseconds(), filePath, oldText, newText),
            "InlineDiffPreviewSignal.WriteRequest");
        return id;
    }

    /// <summary>Polls for the renderer's pickup receipt of <paramref name="id"/>.</summary>
    internal static async Task<bool> WaitForPickupAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = SignalFile.Now + timeout;
        while (SignalFile.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (IsPickedUp(id)) return true;
            await Task.Delay(100, ct);
        }
        return IsPickedUp(id);
    }

    internal static bool IsPickedUp(string id)
    {
        return SignalFile.TryRead<InlineDiffAck>(AckPath)?.Id == id;
    }

    /// <summary>Withdraws an unclaimed request (no renderer picked it up — direct apply follows).</summary>
    internal static void DiscardRequest()
    {
        SignalFile.Delete(RequestPath);
    }

    // ── Renderer side (devenv) ──────────────────────────────────────────────────

    /// <summary>
    /// The pending request targeting <paramref name="filePath"/>, or <c>null</c> when there is
    /// none, it targets another file, its writer process is gone, or it exceeded <see cref="MaxAge"/>.
    /// </summary>
    internal static InlineDiffRequest? TryReadRequestFor(string filePath)
    {
        try
        {
            var request = SignalFile.TryRead<InlineDiffRequest>(RequestPath);
            if (request is null) return null;
            if (!string.Equals(Path.GetFullPath(request.FilePath), Path.GetFullPath(filePath),
                               StringComparison.OrdinalIgnoreCase)) return null;
            if (!SignalFile.IsProcessAlive(request.Pid)) return null;

            var age = SignalFile.Now - DateTimeOffset.FromUnixTimeMilliseconds(request.Ts);
            return age >= TimeSpan.Zero && age < MaxAge ? request : null;
        }
        catch { return null; }
    }

    /// <summary>Claims a request: writes the pickup receipt and consumes the request file, so a
    /// second view of the same document cannot show the preview twice.</summary>
    internal static void Acknowledge(string id)
    {
        SignalFile.Write(AckPath, new InlineDiffAck(id, SignalFile.Now.ToUnixTimeMilliseconds()),
                         "InlineDiffPreviewSignal.Acknowledge");
        SignalFile.Delete(RequestPath);
    }
}
