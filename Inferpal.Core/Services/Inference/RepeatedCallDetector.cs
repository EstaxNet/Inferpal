using System.Text;

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
/// closing tag has arrived and at least two have been seen.
/// </para>
/// </remarks>
internal sealed class RepeatedCallDetector
{
    private const string Close = "</tool_call>";

    private int _scanned;
    private int _closed;

    /// <summary>Whether <paramref name="reasoning"/>, as streamed so far, holds one complete tool call twice.</summary>
    public bool Repeats(StringBuilder reasoning)
    {
        // Back up by less than a tag: one split across two deltas is found, none is counted twice.
        var start = Math.Max(0, _scanned - (Close.Length - 1));
        var fresh = reasoning.ToString(start, reasoning.Length - start);
        _scanned  = reasoning.Length;

        var found = 0;
        for (var i = fresh.IndexOf(Close, StringComparison.Ordinal); i >= 0;
             i = fresh.IndexOf(Close, i + Close.Length, StringComparison.Ordinal))
            found++;
        if (found == 0) return false;
        _closed += found;
        if (_closed < 2) return false;

        var (calls, _) = InlineToolCallParser.TryParse(reasoning.ToString());
        if (calls is not { Count: > 1 }) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls)
            if (!seen.Add(call.Function.Name + "\0" + call.Function.WrittenArguments))
                return true;
        return false;
    }
}
