using System.Text;

namespace Inferpal.Services;

/// <summary>
/// Rolling single-line preview of a reasoning model's chain-of-thought, extracted from
/// the tool-window VM. Thinking models (magistral, deepseek-r1, qwen3-thinking…) stream
/// their reasoning in a separate channel that arrives entirely BEFORE the answer's first
/// content token; without surfacing it a long reasoning phase reads as a broken/empty
/// response. <see cref="Append"/> accumulates deltas and returns the throttled "💭 tail"
/// status text to display, or <c>null</c> when nothing should be posted yet (throttle
/// window, or no printable tail). Deltas only ever arrive from the single stream-reading
/// thread, so no locking is needed.
/// </summary>
internal sealed class ThinkingPreview
{
    private const int BufferCap  = 2000; // bound memory; only the tail is ever shown
    private const int TailLength = 160;
    private const int ThrottleMs = 120;  // limit the Remote-UI RPC rate

    private readonly StringBuilder  _buf = new();
    private readonly Func<DateTime> _clock;
    private DateTime _lastPost = DateTime.MinValue;

    public ThinkingPreview(Func<DateTime>? clock = null) =>
        _clock = clock ?? (static () => DateTime.UtcNow);

    /// <summary>Clears buffered reasoning — stale thought from a finished act must not bleed into the next.</summary>
    public void Reset() => _buf.Clear();

    public string? Append(string delta)
    {
        _buf.Append(delta);
        if (_buf.Length > BufferCap)
            _buf.Remove(0, _buf.Length - BufferCap);

        var now = _clock();
        if ((now - _lastPost).TotalMilliseconds < ThrottleMs)
            return null;
        _lastPost = now;

        var tail = _buf.ToString();
        if (tail.Length > TailLength)
            tail = tail[^TailLength..];
        tail = tail.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return tail.Length == 0 ? null : "\U0001F4AD " + tail;
    }
}
