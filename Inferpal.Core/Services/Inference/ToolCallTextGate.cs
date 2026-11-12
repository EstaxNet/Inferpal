using System.Text;

namespace Inferpal.Services.Inference;

/// <summary>
/// What of a streamed answer reaches the editor while tools are offered: everything, except a tool call the model writes
/// as TEXT (Gemma's <c>&lt;|tool_call&gt;call:…</c>, Qwen's <c>&lt;tool_call&gt;</c>, Mistral's <c>[TOOL_CALLS]</c>…),
/// held from its opening marker until the turn ends.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A piece handed to the editor is never taken back: the bubble appends it, an ACP client appends it, and the answer
/// kept is the bubble (<c>ChatTurnPolicy.PersistedAnswer</c>). Streamed, the call's raw text stayed above its tool card
/// and in the saved conversation, while <see cref="Agent.InlineToolCallParser.FromContent"/> had already turned it into
/// the call that ran.
/// </para>
/// <para>
/// At the end (<see cref="Finish"/>) the editor gets what the turn's content shows beyond what it already has: the
/// content without the call when one was read, the held text when none was — a marker in prose is held, never lost.
/// Only the tagged shapes are held: a bare JSON object is an answer as often as a call, and holding every <c>{</c> would
/// freeze the stream of any answer with code in it.
/// </para>
/// </remarks>
internal sealed class ToolCallTextGate
{
    /// <summary>The markers that open a call written as text, in every shape the parser reads behind a tag.</summary>
    internal static readonly string[] Openers =
        ["<|tool_call>", "<tool_call>", "[TOOL_CALLS]", "<|START_ACTION|>", "<atem:function_calls>"];

    private readonly Action<string>? _onToken;
    private readonly bool _active;
    private readonly StringBuilder _all = new();
    private int _sent;
    private bool _holding;

    /// <param name="onToken">The editor's stream; <c>null</c> when nobody watches.</param>
    /// <param name="toolsOffered">Whether the request offered tools: without them a call shown is content, and passes.</param>
    public ToolCallTextGate(Action<string>? onToken, bool toolsOffered)
    {
        _onToken = onToken;
        _active  = onToken is not null && toolsOffered;
    }

    /// <summary>Hands <paramref name="part"/> on, or the part of it that is not (perhaps) a call.</summary>
    public void Push(string part)
    {
        if (!_active) { _onToken?.Invoke(part); return; }
        _all.Append(part);
        if (_holding) return;
        var text = _all.ToString();
        var from = Math.Max(0, _sent - Openers.Max(o => o.Length));
        var at   = Openers.Select(o => text.IndexOf(o, from, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        if (at >= 0) _holding = true;
        Send(text, at >= 0 ? at : text.Length - PartialOpenerLength(text));
    }

    /// <summary>
    /// The turn's end: sends what <paramref name="shown"/> — the content the turn returns — holds beyond what was sent,
    /// when it still begins with it (it always does, a call only ever being cut from where it was held).
    /// </summary>
    public void Finish(string? shown)
    {
        if (!_active || string.IsNullOrEmpty(shown)) return;
        // ⚠ Compared without the blanks around them: the parser trims what it returns, and a server that opens every
        // turn with "\n\n" (Qwen on LM Studio) would otherwise lose the whole text written after the call.
        var sent = _all.ToString(0, _sent).TrimStart();
        var rest = shown.TrimStart();
        if (!rest.StartsWith(sent, StringComparison.Ordinal)) sent = sent.TrimEnd();
        if (rest.Length > sent.Length && rest.StartsWith(sent, StringComparison.Ordinal))
            _onToken!(rest[sent.Length..]);
        _sent = _all.Length;
    }

    /// <summary>
    /// A turn that ends on an exception (Stop, a dropped stream) never reaches <see cref="Finish"/>: what was held is
    /// shown as it was written — the partial answer kept is the bubble, and prose after a marker would be lost.
    /// </summary>
    public void Release()
    {
        if (!_active || _all.Length <= _sent) return;
        _onToken!(_all.ToString(_sent, _all.Length - _sent));
        _sent = _all.Length;
    }

    private void Send(string text, int upTo)
    {
        if (upTo <= _sent) return;
        _onToken!(text[_sent..upTo]);
        _sent = upTo;
    }

    /// <summary>How many trailing characters of <paramref name="text"/> are the start of an opening marker.</summary>
    internal static int PartialOpenerLength(string text)
    {
        for (var n = Math.Min(text.Length, Openers.Max(o => o.Length) - 1); n > 0; n--)
        {
            var tail = text.AsSpan(text.Length - n);
            foreach (var o in Openers)
                if (o.AsSpan().StartsWith(tail, StringComparison.Ordinal)) return n;
        }
        return 0;
    }
}
