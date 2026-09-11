using Inferpal.Models;

namespace Inferpal.Services.Agent;

/// <summary>
/// Renders tool results as plain conversation, for the histories that can no longer carry a tool
/// <b>block</b> — an <c>assistant</c> message with <c>tool_calls</c> immediately followed by the
/// <c>tool</c> messages that answer them.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A history carrying a tool result <b>without the call that produced it</b> is orphaned in the
/// sense of <see cref="ToolBlockBoundary"/>: Ollama tolerates it, every OpenAI-compatible server
/// rejects it — hence the net in <c>OpenAiCompatibleClient.MapMessages</c>, which <b>drops</b> the
/// result to keep the request valid. Two paths built exactly that shape, and therefore lost every
/// one of their tool results, silently, on one of the two backends: restoring a session or a branch
/// (a saved transcript has no <c>tool_calls</c> — see
/// <see cref="Persistence.SessionManager.BuildRestoredHistory"/>) and the head of the final
/// synthesis (which strips the <c>tool_calls</c> to talk to an empty registry, and left the answers
/// behind it).
/// </para>
/// <para>
/// The content is not lost for that: it becomes a labelled <c>user</c> turn, which both backends
/// accept and no strict template rejects. It is the idiom already used for the compaction summary
/// (<c>[Context Summary]</c>): scaffolding read by the <b>model</b>, hence hard-coded English —
/// this is not interface text.
/// </para>
/// </remarks>
internal static class ToolTranscript
{
    /// <summary>
    /// One tool result as the body of a <c>user</c> turn. <paramref name="toolName"/> may be null
    /// or empty — a restored transcript does not always record which tool answered.
    /// </summary>
    internal static string Render(string? toolName, string? content) =>
        string.IsNullOrEmpty(toolName)
            ? "[Tool result]\n"                 + content
            : $"[Tool result — {toolName}]\n"   + content;

    /// <summary>
    /// Appends one rendered result to <paramref name="history"/>, <b>folded into the preceding
    /// <c>user</c> turn</b> when there is one, and as its own <c>user</c> turn otherwise.
    /// </summary>
    /// <remarks>
    /// ⚠ The folding is not cosmetic. A <c>user</c> turn is the product's unit of counting:
    /// <see cref="HistoryCompaction"/> keeps the last <c>contextWindowKeepTurns</c> by counting
    /// <c>user</c> messages, <c>/branch</c> numbers its turns the same way, and regeneration rolls
    /// the history back to the last <c>user</c>. Opening one turn per tool result would therefore
    /// have shortened the memory of a reloaded session <em>and</em> replayed its last question
    /// twice — one defect traded for another. It is also exactly what
    /// <c>CoalesceConsecutiveRoles</c> would do at send time: we do it at build time, where the
    /// structure counts.
    /// </remarks>
    internal static void Append(List<ChatMessageDto> history, string? toolName, string? content)
    {
        var text = Render(toolName, content);
        var prev = history.Count > 0 ? history[^1] : null;

        if (prev is { Role: "user", ToolCalls: null })
            history[^1] = prev with
            {
                Content = string.IsNullOrEmpty(prev.Content) ? text : prev.Content + "\n\n" + text,
            };
        else
            history.Add(new ChatMessageDto("user", text));
    }

    /// <summary>
    /// The same conversation without a single tool block: every <c>tool</c> answer becomes a
    /// labelled <c>user</c> turn (its name recovered positionally from the call it answers, exactly
    /// as the OpenAI mapping correlates ids), and an <c>assistant</c> turn left with nothing but its
    /// <c>tool_calls</c> is dropped rather than sent with empty content.
    /// <see cref="ToolBlockBoundary.HasOrphanedToolMessage"/> is false on the result.
    /// </summary>
    internal static List<ChatMessageDto> Flatten(IEnumerable<ChatMessageDto> history)
    {
        var flat    = new List<ChatMessageDto>();
        var pending = new Queue<string>();

        foreach (var m in history)
        {
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 } calls)
            {
                pending.Clear();
                foreach (var c in calls) pending.Enqueue(c.Function.Name);

                // An assistant turn that only asked for tools has nothing left to say once the
                // calls are gone: an empty assistant message is noise at best, and some servers
                // reject it outright.
                if (!string.IsNullOrEmpty(m.Content))
                    flat.Add(new ChatMessageDto("assistant", m.Content));
                continue;
            }

            if (m.Role == "tool")
            {
                Append(flat, pending.Count > 0 ? pending.Dequeue() : null, m.Content);
                continue;
            }

            flat.Add(m.ToolCalls is null ? m : m with { ToolCalls = null });
        }

        return flat;
    }
}
