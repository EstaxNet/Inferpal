namespace Inferpal.Services.Inference;

/// <summary>
/// How much of a streamed response the client reads before it stops: what is left of the context window
/// once the prompt is in it, counted generously in characters.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ No chat request carries an output limit (<c>max_tokens</c>, <c>num_predict</c>), and each client's
/// deadline is an INACTIVITY deadline, re-armed by every chunk. A model that never stops — a reasoning model
/// writing the same tool call over and over inside a thinking block it never closes — therefore holds the turn
/// for as long as the server keeps generating, and a server that rolls its context window (LM Studio's overflow
/// policy, Ollama's context shift) keeps generating forever: the user watches "Thinking…" with no end, and the
/// recovery of a tool call written in the reasoning channel, which runs when the stream ends, never runs.
/// </para>
/// <para>
/// A response cannot be longer than the room the window leaves it: past that point a server either stops or
/// produces text that no longer sees its own prompt. The bound is enforced by the CLIENT, not sent to the server:
/// some servers (vLLM) refuse a request whose <c>max_tokens</c> plus prompt exceeds the window, and the prompt size
/// here is an estimate. Counted in characters at <see cref="CharsPerToken"/>, above what a token of prose or code
/// holds, so the client never stops a response before a server that stops at the window would have.
/// </para>
/// </remarks>
internal static class OutputBound
{
    /// <summary>Characters counted per token of room: above prose (~4.5) and code (~3).</summary>
    internal const int CharsPerToken = 5;

    /// <summary>The least room given, when the prompt estimate leaves less (the estimate can overshoot).</summary>
    internal const int MinTokens = 1024;

    /// <summary>The window assumed when none is known: the server's own default is not reported.</summary>
    internal const int UnknownWindow = 32_768;

    /// <summary>The characters a response may stream before the client stops reading it.</summary>
    /// <param name="window">The context window the request is served in, in tokens; 0 or less when unknown.</param>
    /// <param name="promptTokens">The estimated size of the request.</param>
    public static long MaxChars(int window, int promptTokens)
    {
        var room = (window > 0 ? window : UnknownWindow) - Math.Max(0, promptTokens);
        return (long)Math.Max(MinTokens, room) * CharsPerToken;
    }

    /// <summary>The line /diagnostics keeps when the client stopped a response.</summary>
    public static string Note(string model, long chars, int window, int promptTokens) =>
        $"Response from \"{model}\" stopped by Inferpal after {chars} characters: the server was still generating past "
        + $"the room the context window leaves ({(window > 0 ? window : UnknownWindow)} tokens, ~{promptTokens} for "
        + "the request). Treated as cut at the length limit.";
}
