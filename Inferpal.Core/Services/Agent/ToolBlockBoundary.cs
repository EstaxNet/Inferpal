using Inferpal.Models;

namespace Inferpal.Services.Agent;

/// <summary>
/// Keeps history surgery (compaction, truncation, summarisation) from cutting through a
/// <b>tool block</b> — an <c>assistant</c> message carrying <c>tool_calls</c> immediately followed
/// by the <c>tool</c> messages that answer them.
/// </summary>
/// <remarks>
/// <para>
/// Splitting such a block leaves the conversation structurally invalid: either a <c>tool</c> result
/// whose call is gone, or an <c>assistant</c> that asked for calls nobody answered. Ollama tolerates
/// both, which is why the defect stayed invisible on the default backend; every OpenAI-compatible
/// server (LM Studio in OpenAI mode, vLLM, llama.cpp server) rejects the request outright — killing
/// a long agent run exactly when compaction kicks in.
/// </para>
/// <para>
/// Both helpers only ever <em>widen</em> the removed range, never narrow it: validity wins over
/// keeping one extra message (or one extra KV-cache anchor).
/// </para>
/// </remarks>
internal static class ToolBlockBoundary
{
    /// <summary>
    /// Moves the exclusive end of the removed range <c>[start, endExclusive)</c> forward over the
    /// <c>tool</c> messages that the removal would orphan — and only those. The range is replayed
    /// to count the calls it takes away without their answers; a run of <c>tool</c> messages whose
    /// parent stays outside the range is left alone.
    /// </summary>
    public static int SnapEnd(IReadOnlyList<ChatMessageDto> history, int start, int endExclusive)
    {
        var s   = Math.Clamp(start, 0, history.Count);
        var end = Math.Clamp(endExclusive, s, history.Count);

        var pending = 0;
        for (var i = s; i < end; i++)
        {
            var m = history[i];
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 } calls) pending = calls.Count;
            else if (m.Role == "tool" && pending > 0)                         pending--;
        }

        while (pending > 0 && end < history.Count && history[end].Role == "tool")
        {
            end++;
            pending--;
        }
        return end;
    }

    /// <summary>
    /// Moves the inclusive start of a removed range back to swallow an <c>assistant</c> whose
    /// <c>tool_calls</c> would lose their answers. Never goes below <paramref name="floor"/>
    /// (system[0] and anything the caller must keep no matter what).
    /// </summary>
    public static int SnapStart(IReadOnlyList<ChatMessageDto> history, int start, int floor = 1)
    {
        var s = Math.Clamp(start, floor, history.Count);
        while (s > floor && history[s - 1] is { Role: "assistant", ToolCalls.Count: > 0 }) s--;
        return s;
    }

    /// <summary>
    /// True when <paramref name="history"/> contains a <c>tool</c> message that no preceding
    /// <c>assistant</c> tool call can answer, or an <c>assistant</c> whose calls are unanswered
    /// mid-conversation. Used by the tests (and worth asserting after any history rewrite).
    /// </summary>
    public static bool HasOrphanedToolMessage(IReadOnlyList<ChatMessageDto> history)
    {
        var pending = 0;
        for (var i = 0; i < history.Count; i++)
        {
            var m = history[i];
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 } calls)
            {
                // Unanswered calls from an earlier block, then a new block: the earlier one is orphaned.
                if (pending > 0) return true;
                pending = calls.Count;
            }
            else if (m.Role == "tool")
            {
                if (pending == 0) return true;   // answer without a question
                pending--;
            }
            else if (pending > 0)
            {
                return true;                     // a user/assistant turn interleaved into an open block
            }
        }
        return false;                            // trailing unanswered calls are the normal in-flight state
    }
}
