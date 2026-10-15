using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>
/// A turn that wrote nothing printable outside its reasoning — no content, no structured tool call — but did reason.
/// A reasoning model can route its whole turn into the reasoning channel: its tool call, or its final answer. Returned
/// as an empty turn, the call is never run and the answer the user just watched stream as "💭" ends as an empty
/// bubble, under a message that says the server sent no token at all.
/// </summary>
/// <remarks>
/// ⚠ One reader for every chat client: the OpenAI-compatible client (reasoning in <c>reasoning_content</c>) and the
/// Ollama client (reasoning in <c>message.thinking</c>) meet the same turn, and a recovery written in one of them only
/// leaves the other one returning nothing.
/// </remarks>
internal static class ReasoningOnlyTurn
{
    /// <summary>
    /// The turn the reasoning carries: the tool call(s) written in it, else the reasoning itself, promoted as the answer
    /// and flagged <see cref="ChatTurnResult.AnswerIsReasoning"/> — every reader that keeps an answer as content refuses it.
    /// </summary>
    /// <param name="stoppedEarly">The client stopped the stream (bound, loop, repeated call): the reasoning is a model
    /// going round in circles, so the first call it wrote is its intent and every later one is the loop — or its decay.</param>
    /// <param name="offered">The names of the tools the request offered. ⚠ Read as the content is
    /// (<see cref="InlineToolCallParser.FromContent"/>): with none offered — an inline edit, a summary, a commit message —
    /// a reasoning that SHOWS a call is the answer, never a call that a caller with no tool cannot run.</param>
    /// <param name="schemaOf">The offered tools' schemas (<see cref="ToolSchemas.Of"/>): a call written as text has its
    /// values typed by them.</param>
    public static ChatTurnResult Recover(string reasoning, int tokensUsed, int promptTokens, bool cut, bool looping,
                                         bool stoppedEarly, IReadOnlyCollection<string> offered,
                                         Func<string, System.Text.Json.JsonElement?> schemaOf)
    {
        var (calls, _) = InlineToolCallParser.FromContent(reasoning, offered, schemaOf);
        if (stoppedEarly && calls is { Count: > 1 })
            calls = calls.Take(1).ToList();
        if (calls is { Count: > 0 })
            return new ChatTurnResult(string.Empty, calls, tokensUsed, promptTokens, cut, StoppedRepeating: looping);

        return new ChatTurnResult(reasoning.Trim(), null, tokensUsed, promptTokens, cut,
                                  StoppedRepeating: looping, AnswerIsReasoning: true);
    }
}
