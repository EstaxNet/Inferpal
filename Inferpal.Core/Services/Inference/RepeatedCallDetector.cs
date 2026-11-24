using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Inference;

/// <summary>
/// Watches a streamed reasoning channel for the same complete tool call written twice — the sign a model has decided
/// and is now going round in circles.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <see cref="OutputBound"/> guarantees a looping response ends, not that it ends soon: at the room a 32k window
/// leaves, a small model looping inside its thinking block runs for minutes before the client stops it — minutes of
/// "Thinking…" over a call it wrote in its first seconds. A call repeated IDENTICALLY (same name,
/// same arguments) adds nothing to the one before it: the client can stop at the first repeat and run that call,
/// which is what the reasoning-channel recovery runs anyway once the stream ends.
/// </para>
/// <para>
/// Only a repeat: one call drafted in the reasoning before the model closes it and emits the real call is the case
/// the recovery exists for, and must be left to finish. Cheap on the hot path — the full parse runs only when a new
/// closing tag has arrived and at least two have been seen. Calls are read as the recovery reads them
/// (<see cref="InlineToolCallParser.FromContent"/>): with no tool offered, a call the reasoning SHOWS is no call, and
/// stopping there would cut the answer off.
/// </para>
/// </remarks>
/// <param name="offered">The names of the tools the request offered; <c>null</c> reads every call.</param>
/// <param name="schemaOf">Their schemas (<see cref="ToolSchemas.Of"/>).</param>
internal sealed class RepeatedCallDetector(IReadOnlyCollection<string>? offered = null, Func<string, JsonElement?>? schemaOf = null)
{
    // Longer than any closing tag: one split across two deltas is found.
    private const int Lookback = 64;

    private int _scanned;
    private int _counted;   // where the last tag counted ends: none is counted twice
    private int _closed;

    /// <summary>Whether <paramref name="reasoning"/>, as streamed so far, holds one complete tool call twice.</summary>
    public bool Repeats(StringBuilder reasoning)
    {
        var start = Math.Max(_counted, _scanned - Lookback);
        var fresh = reasoning.ToString(start, reasoning.Length - start);
        _scanned  = reasoning.Length;

        var found = 0;
        foreach (System.Text.RegularExpressions.Match m in InlineToolCallParser.CallClosers.Matches(fresh))
        {
            found++;
            _counted = start + m.Index + m.Length;
        }
        if (found == 0) return false;
        _closed += found;
        if (_closed < 2) return false;

        var text = reasoning.ToString();
        var (calls, _) = offered is null ? InlineToolCallParser.TryParse(text) : InlineToolCallParser.FromContent(text, offered, schemaOf);
        if (calls is not { Count: > 1 }) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls)
            if (!seen.Add(call.Function.Name + "\0" + call.Function.WrittenArguments))
                return true;
        return false;
    }
}
