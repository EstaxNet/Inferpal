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
    private readonly byte[] _oneByte = new byte[1];   // read loop only
    private volatile bool _closed;

    /// <summary>
    /// True once the read loop has ended — refused frame, closed stream, broken pipe or disposal. The server
    /// process can still be alive: nothing will ever answer a request on this channel again.
    /// </summary>
    public bool IsClosed => _closed;

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
        // Nobody reads this channel any more: the request would wait out its whole timeout. Checked after the
        // registration, so a loop ending right now either sees this request or is seen by it.
        if (_closed) tcs.TrySetCanceled();

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
                var headers = new FrameHeaderReader();
                var outcome = FrameHeaderReader.Outcome.Pending;
                while (outcome == FrameHeaderReader.Outcome.Pending)
                {
                    if (await _input.ReadAsync(_oneByte.AsMemory(0, 1), _cts.Token) == 0)
                        return; // stream closed
                    outcome = headers.Feed(_oneByte[0]);
                }

                // A frame we refuse is a frame we cannot skip: the body is still sitting in the
                // pipe, and reading on would take it for headers — every message after it parsed as
                // garbage, in silence, for the life of the server. An unreadable length means the
                // sender and this reader no longer agree on where messages begin: end the channel.
                if (outcome != FrameHeaderReader.Outcome.Complete || headers.Length <= 0)
                {
                    Diagnostics.Record("Lsp",
                        $"Refusing a frame ({outcome}, length {headers.Length}); the channel is out of sync and is being closed.");
                    return;
                }
                var contentLength = headers.Length;

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
        catch (Exception ex)
        {
            Diagnostics.Record("Lsp",
                $"The language server's output could not be read ({ex.GetType().Name}: {ex.Message}); it is restarted on the next request.");
        }
        finally
        {
            _closed = true;
            // Fail all pending requests so callers don't hang
            foreach (var tcs in _pending.Values)
                tcs.TrySetCanceled();
            _pending.Clear();
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
