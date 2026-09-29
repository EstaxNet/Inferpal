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
/// gpt-oss writes OpenAI's Harmony format, the same family with named channels:
/// <c>&lt;|channel|&gt;analysis&lt;|message|&gt;</c>…reasoning…<c>&lt;|end|&gt;</c>, <c>&lt;|channel|&gt;final&lt;|message|&gt;</c>…the answer…,
/// and a call as <c>&lt;|channel|&gt;commentary to=functions.NAME &lt;|constrain|&gt;json&lt;|message|&gt;{…}&lt;|call|&gt;</c>. LM Studio
/// parses it — except sometimes, and then the header was the first thing of the answer on screen, or the call was shown
/// as text and the turn ended there.
/// </para>
/// <para>
/// Cohere's turn (North Mini Code, Command) is framed by markers: reasoning up to <c>&lt;|END_THINKING|&gt;</c> (the
/// template writes the opening one), the answer in <c>&lt;|START_TEXT|&gt;</c>…<c>&lt;|END_TEXT|&gt;</c> (<c>&lt;|START_RESPONSE|&gt;</c>
/// on Command A), calls in <c>&lt;|START_ACTION|&gt;</c>…<c>&lt;|END_ACTION|&gt;</c>. A server that does not know the format
/// streams it all as content. The thinking markers become <c>&lt;think&gt;</c> / <c>&lt;/think&gt;</c> — a lone close is the
/// template-opened reasoning every reader already takes out, the webview's own copy included — and the text markers
/// are dropped; the action block is left for <c>InlineToolCallParser</c>, which reads it for every model.
/// </para>
/// <para>
/// Content that does not open that way passes through untouched, as soon as that is certain. A message to another
/// recipient (a tool name) is kept, never dropped: a JSON body — Harmony's arguments, the name being in the header — is
/// rewritten as <c>&lt;tool_call&gt;{"name":…,"arguments":…}&lt;/tool_call&gt;</c>, a form <c>InlineToolCallParser</c> reads for
/// every model; any other body (Muse Glimmer's ATEM block) is kept as it is. A marker split across two chunks is held
/// back until the next one says what it is.
/// </para>
/// </remarks>
internal sealed class ChannelEnvelope
{
    private const string Start     = "<|start|>";
    private const string Role      = "assistant";
    private const string To        = "to=";
    private const string Message   = "<|message|>";
    private const string Channel   = "<|channel>";    // Gemma 4
    private const string Harmony   = "<|channel|>";   // gpt-oss
    private const string Constrain = "<|constrain|>";
    private static readonly string[] Ends   = ["<|eom|>", "<|eot|>", "<|end|>", "<|return|>", "<|call|>", "<channel|>"];
    private static readonly string[] Starts = [Start, Channel, Harmony];

    /// <summary>Past this many characters without a complete header, the text is not an envelope.</summary>
    private const int MaxHeaderChars = 200;

    /// <summary>Cohere's markers and what they become (see the remarks).</summary>
    private static readonly (string Marker, string Becomes)[] CohereMarkers =
    [
        ("<|START_THINKING|>", "<think>"), ("<|END_THINKING|>", "</think>"),
        ("<|START_TEXT|>", ""), ("<|END_TEXT|>", ""), ("<|START_RESPONSE|>", ""), ("<|END_RESPONSE|>", ""),
    ];

    // The tail of the last chunk that may be the beginning of a Cohere marker, held until the next chunk says.
    private string _cohereTail = string.Empty;

    private enum State { Detect, Passthrough, Body }

    private readonly StringBuilder _pending = new();
    // The body of a message to a tool, held until the message ends: its name is in the header, its arguments here.
    private readonly StringBuilder _toolBody = new();
    private State   _state = State.Detect;
    private string? _recipient;

    /// <summary>Whether the stream was recognised as an envelope — false for ordinary content.</summary>
    public bool IsEnvelope { get; private set; }

    /// <summary>Consumes <paramref name="delta"/>; returns what it releases to the answer and to the reasoning.</summary>
    public (string Answer, string Thought) Push(string delta)
    {
        _pending.Append(MapCohereMarkers(delta, final: false));
        return Drain(final: false);
    }

    /// <summary>Releases what was held back, at the end of the stream.</summary>
    public (string Answer, string Thought) Flush()
    {
        _pending.Append(MapCohereMarkers(string.Empty, final: true));
        return Drain(final: true);
    }

    /// <summary>Rewrites Cohere's markers in <paramref name="delta"/>, holding back a marker split across two chunks.</summary>
    private string MapCohereMarkers(string delta, bool final)
    {
        var text = _cohereTail + delta;
        _cohereTail = string.Empty;
        if (text.IndexOf("<|", StringComparison.Ordinal) < 0 && !text.EndsWith('<')) return text;

        foreach (var (marker, becomes) in CohereMarkers)
            text = text.Replace(marker, becomes, StringComparison.Ordinal);
        if (final) return text;

        var lt = text.LastIndexOf('<');
        if (lt >= 0 && CohereMarkers.Any(m => m.Marker.Length > text.Length - lt
                                              && m.Marker.StartsWith(text[lt..], StringComparison.Ordinal)))
        {
            _cohereTail = text[lt..];
            text        = text[..lt];
        }
        return text;
    }

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
                CloseToolMessage(answer);
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

        if (final) CloseToolMessage(answer);   // a call the stream ended in, without its <|call|>
        return (answer.ToString(), thought.ToString());
    }

    private void Route(string text, StringBuilder answer, StringBuilder thought)
    {
        if (text.Length == 0) return;
        switch (_recipient)
        {
            case "self": thought.Append(text); break;
            case "user" or null: answer.Append(text); break;
            default: _toolBody.Append(text); break;
        }
    }

    /// <summary>Releases a finished message to a tool: a JSON body as a call the parser reads, any other as it is.</summary>
    private void CloseToolMessage(StringBuilder answer)
    {
        if (_toolBody.Length == 0) return;
        var body = _toolBody.ToString().Trim();
        _toolBody.Clear();
        if (!IsJsonObject(body))
        {
            answer.Append(body);
            return;
        }
        var name = _recipient!.StartsWith("functions.", StringComparison.Ordinal) ? _recipient["functions.".Length..] : _recipient;
        answer.Append("<tool_call>\n{\"name\": ").Append(System.Text.Json.JsonSerializer.Serialize(name))
              .Append(", \"arguments\": ").Append(body).Append("}\n</tool_call>");
    }

    private static bool IsJsonObject(string text)
    {
        if (!text.StartsWith('{')) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException) { return false; }
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
        if (text.AsSpan(i).StartsWith(Harmony, StringComparison.Ordinal)) return ReadHarmonyHeader(text, i, out recipient);
        if (Channel.StartsWith(text[i..], StringComparison.Ordinal) || Harmony.StartsWith(text[i..], StringComparison.Ordinal))
            return 0;

        if (text.AsSpan(i).StartsWith(Start, StringComparison.Ordinal))
        {
            i = Blanks(text, i + Start.Length);
            if (text.Length - i < Role.Length) return Role.StartsWith(text[i..], StringComparison.Ordinal) ? 0 : -1;
            if (!text.AsSpan(i).StartsWith(Role, StringComparison.Ordinal)) return -1;
            i = Blanks(text, i + Role.Length);
            if (text.AsSpan(i).StartsWith(Harmony, StringComparison.Ordinal)) return ReadHarmonyHeader(text, i, out recipient);
            if (i < text.Length && Harmony.StartsWith(text[i..], StringComparison.Ordinal)) return 0;
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

    /// <summary>
    /// Harmony: <c>&lt;|channel|&gt;NAME</c>, then any of <c>to=RECIPIENT</c>, <c>&lt;|constrain|&gt;</c> and bare words (a content
    /// type such as <c>json</c> or <c>code</c>), then <c>&lt;|message|&gt;</c>. <c>analysis</c> is the reasoning, <c>final</c>
    /// the answer, a message to a recipient a call. Same return convention as <see cref="ReadHeader"/>.
    /// </summary>
    private static int ReadHarmonyHeader(string text, int i, out string recipient)
    {
        recipient = string.Empty;
        var j = i + Harmony.Length;
        var nameAt = j;
        while (j < text.Length && char.IsAsciiLetter(text[j])) j++;
        if (j == text.Length) return 0;
        if (j == nameAt) return -1;
        var channel = text[nameAt..j];
        string? to = null;
        while (true)
        {
            j = Blanks(text, j);
            if (j == text.Length) return 0;
            var rest = text.AsSpan(j);
            if (rest.StartsWith(Message, StringComparison.Ordinal)) { j += Message.Length; break; }
            if (rest.StartsWith(Constrain, StringComparison.Ordinal)) { j += Constrain.Length; continue; }
            if (Message.StartsWith(text[j..], StringComparison.Ordinal) || Constrain.StartsWith(text[j..], StringComparison.Ordinal))
                return 0;
            if (rest.StartsWith(To, StringComparison.Ordinal))
            {
                var at = j += To.Length;
                while (j < text.Length && text[j] != '<' && !char.IsWhiteSpace(text[j])) j++;
                if (j == text.Length) return 0;
                if (j == at) return -1;
                to = text[at..j];
                continue;
            }
            var word = j;
            while (j < text.Length && char.IsAsciiLetterOrDigit(text[j])) j++;
            if (j == text.Length) return 0;
            if (j == word) return -1;
        }
        recipient = channel switch { "analysis" => "self", "final" => "user", _ => to ?? "user" };
        return j;
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
