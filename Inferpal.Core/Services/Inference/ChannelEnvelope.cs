using System.Text;

namespace Inferpal.Services.Inference;

/// <summary>
/// Splits a streamed answer written in a model's channel envelope — Meta Muse Glimmer's addressed messages, Gemma 4's
/// thought channel — into what the model writes to itself (reasoning) and what it writes to the user.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ An assistant turn in this format is a sequence of messages, each addressed:
/// <c>&lt;|start|&gt;assistant to=self&lt;|message|&gt;</c>…private reasoning…<c>&lt;|eom|&gt;</c>, then
/// <c>&lt;|start|&gt;assistant to=user&lt;|message|&gt;</c>…the answer…<c>&lt;|eot|&gt;</c>. A server that does not parse the format
/// for the model streams it all as content — LM Studio does, for Muse Glimmer — and the reasoning and its markers land in
/// the answer: on screen, in the history the model reads back, in every artifact made from a reply. The opening
/// <c>&lt;|start|&gt;assistant</c> belongs to the prompt, so the stream usually opens on <c>to=…&lt;|message|&gt;</c>.
/// </para>
/// <para>
/// Gemma 4 writes its reasoning as <c>&lt;|channel&gt;thought</c>…<c>&lt;channel|&gt;</c> before the answer (an empty one
/// even with thinking off); a server whose reasoning parser misses it leaves it in the content the same way.
/// </para>
/// <para>
/// Content that does not open that way passes through untouched, as soon as that is certain. A message to another
/// recipient (a tool name) is kept as answer rather than dropped: the server turns the model's calls into structured
/// ones, and text that reaches here unparsed is still better shown than lost. A marker split across two chunks is held
/// back until the next one says what it is.
/// </para>
/// </remarks>
internal sealed class ChannelEnvelope
{
    private const string Start   = "<|start|>";
    private const string Role    = "assistant";
    private const string To      = "to=";
    private const string Message = "<|message|>";
    private const string Channel = "<|channel>";   // Gemma 4
    private static readonly string[] Ends   = ["<|eom|>", "<|eot|>", "<|end|>", "<|return|>", "<channel|>"];
    private static readonly string[] Starts = [Start, Channel];

    /// <summary>Past this many characters without a complete header, the text is not an envelope.</summary>
    private const int MaxHeaderChars = 200;

    private enum State { Detect, Passthrough, Body }

    private readonly StringBuilder _pending = new();
    private State   _state = State.Detect;
    private string? _recipient;

    /// <summary>Whether the stream was recognised as an envelope — false for ordinary content.</summary>
    public bool IsEnvelope { get; private set; }

    /// <summary>Consumes <paramref name="delta"/>; returns what it releases to the answer and to the reasoning.</summary>
    public (string Answer, string Thought) Push(string delta)
    {
        _pending.Append(delta);
        return Drain(final: false);
    }

    /// <summary>Releases what was held back, at the end of the stream.</summary>
    public (string Answer, string Thought) Flush() => Drain(final: true);

    private (string Answer, string Thought) Drain(bool final)
    {
        var answer  = new StringBuilder();
        var thought = new StringBuilder();

        while (_pending.Length > 0)
        {
            if (_state == State.Passthrough)
            {
                answer.Append(_pending);
                _pending.Clear();
                break;
            }

            var text = _pending.ToString();
            if (_state == State.Detect)
            {
                var header = ReadHeader(text, out var recipient);
                if (header > 0)
                {
                    IsEnvelope = true;
                    _recipient = recipient;
                    _state     = State.Body;
                    _pending.Remove(0, header);
                    continue;
                }
                if (header == 0 && !final && text.Length <= MaxHeaderChars) break;   // could still be a header: wait
                // Not a header. Before any message the content is ordinary; after one, text with no address of its
                // own is taken for the answer — never for the reasoning of the message before it.
                if (!IsEnvelope)
                {
                    _state = State.Passthrough;
                    continue;
                }
                _recipient = "user";
                _state     = State.Body;
            }

            // Body: up to the next marker, to the message's recipient.
            var (at, length) = NextMarker(text);
            if (at >= 0)
            {
                Route(text[..at], answer, thought);
                var marker = text.Substring(at, length);
                // An end marker closes the message; a start marker opens the next one, read again as a header.
                _pending.Remove(0, Starts.Contains(marker) ? at : at + length);
                _state = State.Detect;
                continue;
            }

            var keep = final ? 0 : HeldBack(text);
            Route(text[..^keep], answer, thought);
            _pending.Remove(0, text.Length - keep);
            break;
        }

        return (answer.ToString(), thought.ToString());
    }

    private void Route(string text, StringBuilder answer, StringBuilder thought)
    {
        if (text.Length == 0) return;
        (_recipient == "self" ? thought : answer).Append(text);
    }

    /// <summary>
    /// The length of a complete header at the start of <paramref name="text"/> (optional <c>&lt;|start|&gt;assistant</c>,
    /// then <c>to=NAME&lt;|message|&gt;</c>, blanks allowed between); 0 when the text may still become one; -1 when it cannot.
    /// </summary>
    private static int ReadHeader(string text, out string recipient)
    {
        recipient = string.Empty;
        var i = Blanks(text, 0);
        if (i == text.Length) return 0;

        // Gemma 4: <|channel>thought — the name ends at the first character that is not a letter.
        if (text.AsSpan(i).StartsWith(Channel, StringComparison.Ordinal))
        {
            var nameAt = i + Channel.Length;
            var end    = nameAt;
            while (end < text.Length && char.IsAsciiLetter(text[end])) end++;
            if (end == text.Length) return 0;                         // the name may go on in the next chunk
            if (end == nameAt) return -1;
            recipient = text[nameAt..end] == "thought" ? "self" : "user";
            return end;
        }
        if (Channel.StartsWith(text[i..], StringComparison.Ordinal)) return 0;

        if (text.AsSpan(i).StartsWith(Start, StringComparison.Ordinal))
        {
            i = Blanks(text, i + Start.Length);
            if (text.Length - i < Role.Length) return Role.StartsWith(text[i..], StringComparison.Ordinal) ? 0 : -1;
            if (!text.AsSpan(i).StartsWith(Role, StringComparison.Ordinal)) return -1;
            i = Blanks(text, i + Role.Length);
        }
        else if (Start.StartsWith(text[i..], StringComparison.Ordinal)) return 0;

        if (text.Length - i < To.Length) return To.StartsWith(text[i..], StringComparison.Ordinal) ? 0 : -1;
        if (!text.AsSpan(i).StartsWith(To, StringComparison.Ordinal)) return -1;
        i += To.Length;

        var nameStart = i;
        while (i < text.Length && text[i] != '<' && !char.IsWhiteSpace(text[i])) i++;
        var name = text[nameStart..i];
        i = Blanks(text, i);
        if (i == text.Length) return 0;
        if (name.Length == 0) return -1;
        if (text.Length - i < Message.Length) return Message.StartsWith(text[i..], StringComparison.Ordinal) ? 0 : -1;
        if (!text.AsSpan(i).StartsWith(Message, StringComparison.Ordinal)) return -1;

        recipient = name;
        return i + Message.Length;
    }

    private static int Blanks(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    /// <summary>The first end or start marker in <paramref name="text"/>, and its length.</summary>
    private static (int At, int Length) NextMarker(string text)
    {
        var (at, length) = (-1, 0);
        foreach (var marker in Ends.Concat(Starts))
        {
            var i = text.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0 && (at < 0 || i < at)) (at, length) = (i, marker.Length);
        }
        return (at, length);
    }

    /// <summary>How many trailing characters may be the beginning of a marker, and must wait for the next chunk.</summary>
    private static int HeldBack(string text)
    {
        var lt = text.LastIndexOf('<');
        if (lt < 0) return 0;
        var tail = text[lt..];
        return Ends.Concat(Starts).Any(m => m.StartsWith(tail, StringComparison.Ordinal)) ? tail.Length : 0;
    }
}
