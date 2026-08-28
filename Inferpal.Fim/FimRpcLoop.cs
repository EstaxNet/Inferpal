using System.Text;
using System.Text.Json;

namespace Inferpal.Fim;

/// <summary>What a completion request carries.</summary>
internal sealed record FimRequest(
    string Prefix, string Suffix, int MaxTokens, double Temperature, string? Model);

/// <summary>
/// The sidecar service loop: reads header-framed JSON-RPC 2.0 messages
/// (<c>Content-Length</c>, UTF-8 body) from one stream and answers on another.
/// </summary>
/// <remarks>
/// <para>
/// Two methods only. <c>fim/complete</c> (request) returns the whole completion, never a
/// transport error for an ordinary condition: a backend without FIM, a cancellation or a silent
/// model all return the empty string, which the caller shows as "nothing to suggest".
/// <c>fim/cancel</c> (notification) cancels an in-flight request by id - and that is the NORMAL
/// case here, not the exceptional one: the user types, and every keystroke makes the pending
/// completion stale.
/// </para>
/// <para>
/// Requests are not serialized: an in-flight completion keeps going while the loop reads the next
/// message, otherwise the cancellation would arrive after the answer it was meant to avoid.
/// </para>
/// <para>
/// The loop returns when the input stream closes - that is, when devenv dies. The sidecar never
/// outlives the editor that started it.
/// </para>
/// </remarks>
internal sealed class FimRpcLoop
{
    /// <summary>Framing guard rail: a body larger than this is not a completion request.</summary>
    internal const int MaxBodyBytes = 8 * 1024 * 1024;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Func<FimRequest, CancellationToken, Task<string>> _complete;

    private readonly object _gate = new();
    private readonly Dictionary<int, CancellationTokenSource> _inflight = [];

    internal FimRpcLoop(Stream input, Stream output,
                        Func<FimRequest, CancellationToken, Task<string>> complete)
    {
        _input    = input;
        _output   = output;
        _complete = complete;
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var body = await ReadMessageAsync(ct).ConfigureAwait(false);
            if (body is null) break;                       // stdin closed: the editor is gone

            try { Dispatch(body, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Diagnostics.Swallow("FimRpcLoop.Dispatch", ex); }
        }

        // Nobody must stay in flight behind a loop that is stopping.
        lock (_gate)
        {
            foreach (var cts in _inflight.Values) { try { cts.Cancel(); } catch { /* nettoyage */ } }
            _inflight.Clear();
        }
    }

    private void Dispatch(string body, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(body);
        var root   = doc.RootElement;
        var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;

        if (method == "fim/cancel")
        {
            if (root.TryGetProperty("params", out var cancelParams) &&
                cancelParams.TryGetProperty("id", out var cancelId) &&
                cancelId.ValueKind == JsonValueKind.Number)
                CancelInflight(cancelId.GetInt32());
            return;
        }

        if (method != "fim/complete") return;
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number) return;

        var id = idElement.GetInt32();
        if (!root.TryGetProperty("params", out var p)) { WriteResult(id, string.Empty); return; }

        var request = new FimRequest(
            Prefix:      Str(p, "prefix") ?? string.Empty,
            Suffix:      Str(p, "suffix") ?? string.Empty,
            MaxTokens:   Num(p, "maxTokens", 128),
            Temperature: Dbl(p, "temperature", 0.2),
            Model:       Str(p, "model"));

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _inflight[id] = cts;

        // Deliberately detached: the loop must stay free to read the cancellation that follows.
        _ = Task.Run(async () =>
        {
            var text = string.Empty;
            try { text = await _complete(request, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diagnostics.Swallow("FimRpcLoop.Complete", ex); }
            finally
            {
                lock (_gate) { if (_inflight.Remove(id)) cts.Dispose(); }
            }
            WriteResult(id, text);
        }, CancellationToken.None);
    }

    private void CancelInflight(int id)
    {
        CancellationTokenSource? cts;
        lock (_gate) { _inflight.TryGetValue(id, out cts); }
        try { cts?.Cancel(); } catch (Exception ex) { Diagnostics.Swallow("FimRpcLoop.Cancel", ex); }
    }

    // ── Cadrage ───────────────────────────────────────────────────────────────

    private void WriteResult(int id, string text)
    {
        try
        {
            var json = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":"
                       + JsonSerializer.Serialize(text) + "}";
            var body   = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            lock (_output)
            {
                _output.Write(header, 0, header.Length);
                _output.Write(body, 0, body.Length);
                _output.Flush();
            }
        }
        catch (Exception ex) { Diagnostics.Swallow("FimRpcLoop.Write", ex); }
    }

    /// <summary>Reads a complete message. <c>null</c> when the stream is closed.</summary>
    private async Task<string?> ReadMessageAsync(CancellationToken ct)
    {
        var length = await ReadHeadersAsync(ct).ConfigureAwait(false);
        if (length < 0) return null;

        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await _input.ReadAsync(body.AsMemory(read, length - read), ct).ConfigureAwait(false);
            if (n <= 0) return null;
            read += n;
        }
        return Encoding.UTF8.GetString(body);
    }

    private async Task<int> ReadHeadersAsync(CancellationToken ct)
    {
        var line   = new StringBuilder();
        var length = -1;
        var any    = false;
        var one    = new byte[1];

        while (true)
        {
            var n = await _input.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n <= 0) return -1;

            var b = one[0];
            if (b != (byte)'\n') { if (b != (byte)'\r') line.Append((char)b); continue; }

            var text = line.ToString();
            line.Clear();
            if (text.Length == 0) return any ? length : -1;

            any = true;
            const string marker = "Content-Length:";
            if (text.StartsWith(marker, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(text[marker.Length..].Trim(), out var parsed) &&
                parsed >= 0 && parsed <= MaxBodyBytes)
                length = parsed;
        }
    }

    // ── Defensive parameter reading ───────────────────────────────────────────

    private static string? Str(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Num(JsonElement p, string name, int fallback) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : fallback;

    private static double Dbl(JsonElement p, string name, double fallback) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d : fallback;
}
