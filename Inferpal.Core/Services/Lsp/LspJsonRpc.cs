using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Lsp;

/// <summary>
/// Minimal JSON-RPC 2.0 transport over a process stdin/stdout byte stream.
/// Implements the LSP Content-Length framing protocol.
/// Thread-safe: multiple concurrent requests are supported via a dictionary of
/// pending <see cref="TaskCompletionSource{T}"/> keyed by request ID.
/// </summary>
internal sealed class LspJsonRpc : IDisposable
{
    private readonly Stream _output;            // process stdin  (we write to it)
    private readonly Stream _input;             // process stdout (we read from it)
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private int _nextId;

    private static readonly JsonSerializerOptions SerOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public LspJsonRpc(Stream output, Stream input)
    {
        _output = output;
        _input  = input;
        _ = Task.Run(ReadLoopAsync);
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a JSON-RPC request and awaits the response, with an optional timeout.
    /// Returns <c>null</c> if the server returns a null result or the request times out.
    /// </summary>
    public async Task<JsonElement?> SendRequestAsync(
        string method, object? @params, CancellationToken ct, TimeSpan? timeout = null)
    {
        int id  = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            if (timeout.HasValue) linked.CancelAfter(timeout.Value);

            await WriteAsync(new LspRequest { Id = id, Method = method, Params = @params }, linked.Token);

            await using (linked.Token.Register(() => tcs.TrySetCanceled(linked.Token)))
                return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a JSON-RPC notification (no response expected).</summary>
    public Task SendNotificationAsync(string method, object? @params, CancellationToken ct) =>
        WriteAsync(new LspRequest { Method = method, Params = @params }, ct);

    // ── Write ──────────────────────────────────────────────────────────────────

    private async Task WriteAsync(LspRequest msg, CancellationToken ct)
    {
        var body   = JsonSerializer.SerializeToUtf8Bytes(msg, SerOpts);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await _writeLock.WaitAsync(ct);
        try
        {
            await _output.WriteAsync(header, ct);
            await _output.WriteAsync(body,   ct);
            await _output.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── Read loop ──────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // ── Parse headers ─────────────────────────────────────────────
                int contentLength = 0;
                string? line;

                while ((line = await ReadHeaderLineAsync(_input, _cts.Token)) is not null)
                {
                    if (line.Length == 0) break; // blank line = end of headers

                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(line["Content-Length:".Length..].Trim(), out var cl))
                    {
                        contentLength = cl;
                    }
                }

                // A frame we refuse is a frame we cannot skip: the body is still sitting in the
                // pipe, and `continue` used to go read it as if it were headers — every message
                // after it parsed as garbage, in silence, for the life of the server. The two
                // sibling readers of this same framing (FimSidecar, FimRpcLoop) end the loop
                // instead, and that is the right answer: an unreadable length means the sender and
                // this reader no longer agree on where messages begin.
                if (contentLength <= 0 || contentLength > MaxBodyBytes)
                {
                    Diagnostics.Record("Lsp",
                        $"Refusing a frame declaring {contentLength} bytes; the channel is out of sync and is being closed.");
                    return;
                }

                // ── Read body as raw bytes (Content-Length is a byte count) ───
                var body  = new byte[contentLength];
                int total = 0;

                while (total < contentLength)
                {
                    int n = await _input.ReadAsync(body.AsMemory(total, contentLength - total), _cts.Token);
                    if (n == 0) return; // stream closed
                    total += n;
                }

                DispatchMessage(Encoding.UTF8.GetString(body));
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch { /* server crashed or pipe broken */ }
        finally
        {
            // Fail all pending requests so callers don't hang
            foreach (var tcs in _pending.Values)
                tcs.TrySetCanceled();
            _pending.Clear();
        }
    }

    /// <summary>Reads one text line from <paramref name="stream"/> byte-by-byte.</summary>
    /// <summary>
    /// Ceiling on a single message body. The two sibling readers of this framing cap at 8 MB;
    /// this one allowed 100 MB and allocated the array before reading a byte, so a bogus header
    /// bought a 100 MB allocation. A language server's largest real message — a full workspace
    /// symbol dump — is orders of magnitude below this.
    /// </summary>
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>A header line longer than this is not a header line.</summary>
    private const int MaxHeaderLineChars = 4 * 1024;

    private static async Task<string?> ReadHeaderLineAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[1];
        var sb  = new StringBuilder(64);

        while (true)
        {
            if (await stream.ReadAsync(buf.AsMemory(0, 1), ct) == 0)
                return null; // EOF

            // Binary on the wire (a desynced stream, a server printing to stdout) contains no
            // newline for as long as it lasts, and this builder grew for all of it.
            if (sb.Length > MaxHeaderLineChars) return null;

            byte b = buf[0];
            if (b == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r')
                    sb.Remove(sb.Length - 1, 1);
                return sb.ToString();
            }

            sb.Append((char)b);
        }
    }

    private void DispatchMessage(string json)
    {
        try
        {
            var resp = JsonSerializer.Deserialize<LspResponse>(json);
            if (resp?.Id is int id && _pending.TryRemove(id, out var tcs))
            {
                // result can legitimately be null (JsonValueKind.Null) for some methods
                tcs.TrySetResult(resp.Result?.ValueKind == JsonValueKind.Null ? null : resp.Result);
            }
            // Notifications (no id) are silently dropped — we don't use them
        }
        catch { /* malformed JSON — ignore */ }
    }

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
