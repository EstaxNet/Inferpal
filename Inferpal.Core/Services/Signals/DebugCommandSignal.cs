using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inferpal.Services.Debugging;

namespace Inferpal.Services.Signals;

/// <summary>One debugger operation asked of the in-process driver.</summary>
internal sealed record DebugCommandRequest(
    [property: JsonPropertyName("id")]    string Id,
    [property: JsonPropertyName("pid")]   int Pid,
    [property: JsonPropertyName("ts")]    long Ts,
    [property: JsonPropertyName("op")]    string Op,
    [property: JsonPropertyName("file")]  string? File = null,
    [property: JsonPropertyName("line")]  int Line = 0,
    [property: JsonPropertyName("expr")]  string? Expression = null,
    [property: JsonPropertyName("frame")] int? FrameId = null);

/// <summary>The in-process driver's answer, correlated by <see cref="Id"/>.</summary>
internal sealed record DebugCommandResponse(
    [property: JsonPropertyName("id")]    string Id,
    [property: JsonPropertyName("ok")]    bool Ok,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("state")] DebugStopState? State = null,
    [property: JsonPropertyName("flag")]  bool Flag = false,
    [property: JsonPropertyName("text")]  string? Text = null,
    [property: JsonPropertyName("bps")]   IReadOnlyList<DebugBreakpointInfo>? Breakpoints = null);

/// <summary>
/// File-based IPC carrying debugger <b>commands</b> from the out-of-process extension host to the
/// in-process package in devenv, and their answers back.
/// </summary>
/// <remarks>
/// <para>
/// The state direction already existed (<see cref="DebuggerStateSignal"/>: the in-process tracker
/// publishes break snapshots). This is the reverse leg, and it is the one §21 needed. Same
/// transport family as <see cref="InlineDiffPreviewSignal"/> — request file, answer file, PID guard,
/// expiry — with correlation ids added because, unlike a preview, several operations follow one
/// another and a stale answer must never be mistaken for the current one.
/// </para>
/// <para>
/// ⚠ Deliberately <b>not</b> a queue: one request at a time. A debugger is a single stateful
/// machine, and two interleaved <c>continue</c> calls would race for the same stop event. The
/// caller (<see cref="SignalDebugSession"/>) serialises.
/// </para>
/// </remarks>
internal static class DebugCommandSignal
{
    internal static string RequestPath  => SignalFile.PathFor("debug_request.json");
    internal static string ResponsePath => SignalFile.PathFor("debug_response.json");

    /// <summary>Written by the in-process driver while it is able to serve requests.</summary>
    internal static string ReadyPath => SignalFile.PathFor("debug_ready.json");

    /// <summary>A request older than this is ignored (host died between write and read).</summary>
    internal static TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(10);

    private sealed record ReadyMarker([property: JsonPropertyName("pid")] int Pid);

    // ── Host side (tools) ───────────────────────────────────────────────────────────

    /// <summary><c>true</c> when a live devenv is advertising a debugger driver.</summary>
    internal static bool IsDriverReady()
    {
        try
        {
            var marker = SignalFile.TryRead<ReadyMarker>(ReadyPath);
            return marker is not null && SignalFile.IsProcessAlive(marker.Pid);
        }
        catch { return false; }
    }

    /// <summary>Publishes a request and returns its id, or <c>null</c> when it could not be written.</summary>
    internal static string? WriteRequest(DebugCommandRequest request)
    {
        try
        {
            Directory.CreateDirectory(SignalFile.Dir);
            // A leftover answer must never satisfy this request's wait.
            SignalFile.Delete(ResponsePath);
            File.WriteAllText(RequestPath, JsonSerializer.Serialize(request));
            return request.Id;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("DebugCommandSignal.WriteRequest", ex);
            return null;
        }
    }

    /// <summary>Polls for the answer to <paramref name="id"/>. <c>null</c> on timeout.</summary>
    internal static async Task<DebugCommandResponse?> WaitForResponseAsync(
        string id, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = SignalFile.Now + timeout;
        while (SignalFile.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var response = TryReadResponse(id);
            if (response is not null) return response;
            await Task.Delay(60, ct);
        }
        return TryReadResponse(id);
    }

    internal static DebugCommandResponse? TryReadResponse(string id)
    {
        try
        {
            var response = SignalFile.TryRead<DebugCommandResponse>(ResponsePath);
            return response?.Id == id ? response : null;
        }
        catch { return null; }
    }

    /// <summary>Withdraws an unanswered request so it cannot be executed late.</summary>
    internal static void DiscardRequest()
    {
        SignalFile.Delete(RequestPath);
    }

    // ── Driver side (devenv, in-process) ────────────────────────────────────────────

    /// <summary>Advertises that this process can drive the debugger.</summary>
    internal static void MarkReady(int pid)
    {
        try
        {
            SignalFile.Write(ReadyPath, new ReadyMarker(pid), "DebugCommandSignal.MarkReady");
        }
        catch (Exception ex) { Diagnostics.Swallow("DebugCommandSignal.MarkReady", ex); }
    }

    /// <summary>Withdraws the advertisement (package disposed, VS closing).</summary>
    internal static void ClearReady()
    {
        SignalFile.Delete(ReadyPath);
    }

    /// <summary>
    /// Claims the pending request, consuming it so it cannot be executed twice. <c>null</c> when
    /// there is none, its writer is gone, or it exceeded <see cref="MaxAge"/>.
    /// </summary>
    internal static DebugCommandRequest? ClaimRequest()
    {
        try
        {
            var request = SignalFile.TryRead<DebugCommandRequest>(RequestPath);
            SignalFile.Delete(RequestPath);
            if (request is null || !SignalFile.IsProcessAlive(request.Pid)) return null;

            var age = SignalFile.Now - DateTimeOffset.FromUnixTimeMilliseconds(request.Ts);
            return age >= TimeSpan.Zero && age < MaxAge ? request : null;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("DebugCommandSignal.ClaimRequest", ex);
            return null;
        }
    }

    internal static void WriteResponse(DebugCommandResponse response)
    {
        try
        {
            SignalFile.Write(ResponsePath, response, "DebugCommandSignal.WriteResponse");
        }
        catch (Exception ex) { Diagnostics.Swallow("DebugCommandSignal.WriteResponse", ex); }
    }
}
