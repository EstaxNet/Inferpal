using System.Globalization;
using System.Text;
using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.CodeActions;

namespace Inferpal.Host;

/// <summary>
/// Announces every tool call to the adapter when it starts and when it ends (<c>chat/toolStart</c>,
/// <c>chat/toolEnd</c>), each under an id of its own — what an adapter needs to draw a call while it runs and to tie
/// the approval it raises to it (<see cref="InitializeParams.ToolEvents"/>).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The loop's own report (<c>onToolExecuted</c>, the <c>chat/tool</c> notification) comes AFTER the call returned: a
/// command that runs for a minute, or an approval card the user reads for one, has no call on screen until it is over,
/// and the card cannot point at the call it is about. The start is only visible from here, around the registry.
/// </para>
/// <para>
/// ⚠ Called concurrently: a batch of read-only tools runs in parallel. The id of the running call travels in an
/// <see cref="AsyncLocal{T}"/> — the approval asked inside a call reads its own, never a neighbour's.
/// </para>
/// </remarks>
internal sealed class ToolEventRegistry(
    IToolRegistry inner, Action<string, string, string> onStart, Action<string, string> onEnd) : IToolRegistry
{
    private static readonly AsyncLocal<string?> Current = new();
    private static long _next;

    /// <summary>The id of the call running in this asynchronous flow; <c>null</c> outside one.</summary>
    public static string? CurrentCallId => Current.Value;

    public IReadOnlyList<ToolDefinition> Definitions => inner.Definitions;
    public DiffInfo? ConsumeDiff() => inner.ConsumeDiff();
    public int? WritesInRun => inner.WritesInRun;

    public async Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        var id = "call_" + Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
        onStart(id, name, args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText());
        var previous = Current.Value;
        Current.Value = id;
        var output = string.Empty;
        try
        {
            output = await inner.ExecuteAsync(name, args, ct);
            return output;
        }
        finally
        {
            Current.Value = previous;
            // Every way out ends the call on screen: a stop mid-call would otherwise leave it running for ever.
            onEnd(id, output);
        }
    }
}

/// <summary>
/// The model's reasoning in pieces an adapter can append (<c>chat/reasoning</c>): what arrived since the last piece, at
/// most every <see cref="Interval"/>, and the rest on <see cref="Flush"/>.
/// </summary>
/// <remarks>
/// ⚠ Never one message per delta: a reasoning model sends thousands of them in one thinking phase, and each one would be
/// a JSON-RPC message over stdio. Never a tail either (that is <c>chat/thinking</c>'s bounded preview): appended, a
/// tail repeats what the reader already has.
/// </remarks>
internal sealed class ReasoningBatch(Action<string> emit, Func<DateTime>? clock = null)
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(120);

    private readonly StringBuilder _pending = new();
    private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);
    private readonly object _gate = new();
    private DateTime _last = DateTime.MinValue;

    public void Append(string delta)
    {
        string? piece = null;
        lock (_gate)
        {
            _pending.Append(delta);
            var now = _clock();
            if (now - _last >= Interval)
            {
                piece = _pending.ToString();
                _pending.Clear();
                _last = now;
            }
        }
        if (!string.IsNullOrEmpty(piece)) emit(piece);
    }

    /// <summary>Sends what is still pending — at the end of the turn, or before the answer starts.</summary>
    public void Flush()
    {
        string piece;
        lock (_gate)
        {
            piece = _pending.ToString();
            _pending.Clear();
        }
        if (piece.Length > 0) emit(piece);
    }
}
