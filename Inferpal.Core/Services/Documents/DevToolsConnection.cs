using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Inferpal.Services.Documents;

/// <summary>A DevTools call the browser answered with an error: the browser is alive, the call did not happen.</summary>
internal sealed class DevToolsCallException(string message) : Exception(message);

/// <summary>
/// One page's DevTools protocol connection: calls answered by their id, events read as they come by a pump of their own.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The pump reads while no call waits: a page that opens <c>alert()</c> or <c>confirm()</c> holds the click that opened it
/// until the dialog is answered, so the answer is sent from the event (<see cref="Post"/>), never from the call that waits.
/// </para>
/// <para>
/// Only the events a caller waits for are counted (<c>watched</c>, <see cref="Record"/>): a page that logs at every frame
/// would otherwise grow the list for as long as the page stays open.
/// </para>
/// </remarks>
internal sealed class DevToolsConnection : IDisposable
{
    /// <summary>The failure every call gets once the browser's connection is gone.</summary>
    private static WebSocketException Ended(Exception why) => new("the browser's DevTools connection ended: " + why.Message);

    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _waiting = new();
    private readonly List<string> _seen = [];
    private readonly IReadOnlyCollection<string> _watched;
    private readonly Action<DevToolsConnection, string, JsonElement>? _onEvent;
    private readonly CancellationTokenSource _closed = new();
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _lost;
    private int _next;

    private DevToolsConnection(ClientWebSocket socket, IReadOnlyCollection<string> watched,
                               Action<DevToolsConnection, string, JsonElement>? onEvent) =>
        (_socket, _watched, _onEvent) = (socket, watched, onEvent);

    /// <summary>Connects to a page target's socket and starts reading.</summary>
    /// <param name="watched">The events counted as they arrive, for <see cref="SeenSince"/> and <see cref="WaitForAsync"/>.</param>
    /// <param name="onEvent">Called on the pump for each event, with its name and params: it may <see cref="Post"/> or
    /// <see cref="Record"/>, never wait.</param>
    internal static async Task<DevToolsConnection> ConnectAsync(string socketUrl, IReadOnlyCollection<string> watched,
                                                                Action<DevToolsConnection, string, JsonElement>? onEvent,
                                                                CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        try { await socket.ConnectAsync(new Uri(socketUrl), ct).ConfigureAwait(false); }
        catch { socket.Dispose(); throw; }
        var connection = new DevToolsConnection(socket, watched, onEvent);
        _ = Task.Run(connection.PumpAsync);
        return connection;
    }

    /// <summary>Whether the connection ended: the browser exited, or closed it.</summary>
    internal bool Lost => Volatile.Read(ref _lost) is not null;

    /// <summary>How many events have been counted: a mark that <see cref="SeenSince"/> and <see cref="WaitForAsync"/> count from.</summary>
    internal int Mark
    {
        get { lock (_seen) return _seen.Count; }
    }

    /// <summary>Whether <paramref name="eventName"/> was counted after <paramref name="mark"/>.</summary>
    internal bool SeenSince(int mark, string eventName)
    {
        lock (_seen)
            for (var i = Math.Min(mark, _seen.Count); i < _seen.Count; i++)
                if (_seen[i] == eventName) return true;
        return false;
    }

    /// <summary>Counts <paramref name="eventName"/>, as if the browser had sent it: an event handler's own reading of one
    /// (the main frame's start of a load, where the browser says it of every frame).</summary>
    internal void Record(string eventName)
    {
        TaskCompletionSource arrived;
        lock (_seen)
        {
            _seen.Add(eventName);
            arrived = _arrived;
            _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        arrived.TrySetResult();
    }

    /// <summary>Waits for <paramref name="eventName"/> to be counted after <paramref name="mark"/>.</summary>
    internal async Task WaitForAsync(string eventName, int mark, CancellationToken ct)
    {
        while (true)
        {
            Task arrived;
            lock (_seen)
            {
                if (SeenSince(mark, eventName)) return;
                if (_lost is { } lost) throw Ended(lost);
                arrived = _arrived.Task;
            }
            await arrived.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Calls <paramref name="method"/> and waits for its answer.</summary>
    /// <exception cref="DevToolsCallException">The browser answered with an error.</exception>
    /// <exception cref="WebSocketException">The connection ended.</exception>
    internal async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _next);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = answer;
        try
        {
            if (Volatile.Read(ref _lost) is { } lost) throw Ended(lost);
            await SendAsync(id, method, parameters, ct).ConfigureAwait(false);
            return await answer.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (DevToolsCallException ex)
        {
            throw new DevToolsCallException($"{method}: {ex.Message}");
        }
        finally { _waiting.TryRemove(id, out _); }
    }

    /// <summary>Sends <paramref name="method"/> without waiting for its answer: what an event handler on the pump may do.</summary>
    internal void Post(string method, object parameters) =>
        _ = SendAsync(Interlocked.Increment(ref _next), method, parameters, _closed.Token)
            .ContinueWith(t => Diagnostics.Swallow("DevToolsConnection.Post", t.Exception!.GetBaseException()),
                          TaskContinuationOptions.OnlyOnFaulted);

    private async Task SendAsync(int id, string method, object parameters, CancellationToken ct)
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await _socket.SendAsync(request, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                message.SetLength(0);
                WebSocketReceiveResult part;
                do
                {
                    part = await _socket.ReceiveAsync(buffer, _closed.Token).ConfigureAwait(false);
                    if (part.MessageType == WebSocketMessageType.Close) throw new WebSocketException("the browser closed its DevTools connection");
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);

                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var answered) && answered.TryGetInt32(out var id))
                {
                    if (_waiting.TryGetValue(id, out var waiting))
                    {
                        if (root.TryGetProperty("error", out var error))
                            waiting.TrySetException(new DevToolsCallException(
                                error.TryGetProperty("message", out var said) && said.GetString() is { } text ? text : error.GetRawText()));
                        else waiting.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                    }
                    continue;
                }
                if (!root.TryGetProperty("method", out var name) || name.GetString() is not { } eventName) continue;
                try { _onEvent?.Invoke(this, eventName, root.TryGetProperty("params", out var p) ? p : default); }
                catch (Exception ex) { Diagnostics.Swallow("DevToolsConnection.OnEvent", ex); }
                if (_watched.Contains(eventName)) Record(eventName);
            }
        }
        catch (Exception ex)
        {
            if (ex is not (OperationCanceledException or WebSocketException or JsonException or IOException or ObjectDisposedException))
                Diagnostics.Swallow("DevToolsConnection.Pump", ex);
            Lose(ex);
        }
    }

    private void Lose(Exception why)
    {
        TaskCompletionSource arrived;
        lock (_seen)
        {
            _lost ??= why;
            arrived = _arrived;
        }
        foreach (var waiting in _waiting.Values)
            waiting.TrySetException(Ended(why));
        arrived.TrySetResult();
    }

    public void Dispose()
    {
        // ⚠ _closed is never disposed: the pump and a late Post still read its token.
        _closed.Cancel();
        Lose(new ObjectDisposedException(nameof(DevToolsConnection)));
        _socket.Dispose();
    }
}
