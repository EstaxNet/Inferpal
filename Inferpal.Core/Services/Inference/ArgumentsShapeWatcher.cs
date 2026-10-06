using System.Text;
using System.Text.Json;
using Inferpal.Services.Agent;

namespace Inferpal.Services.Inference;

/// <summary>
/// Reads the streamed arguments of each structured tool call as they arrive, and says the moment they can no longer
/// become what the tool reads.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A quote left unescaped inside a string value — the <c>$"</c> of a C# interpolated string copied into
/// <c>old_content</c> — ends that string in the middle of the code. Devstral then goes on writing strings separated by
/// <c>" ,"</c> for minutes: the text stays LEXICALLY valid JSON to the end (an array of strings), no event reaches the
/// front-ends, and the varying paths and keys keep <see cref="ArgumentsLoopDetector"/> silent. Only the tool's schema
/// sees it, a few hundred characters in: an element of <c>edits</c> that begins as text where an edit object goes.
/// </para>
/// <para>
/// Three facts, each one final — no later text can turn the arguments back into a call the tool runs:
/// <list type="bullet">
///   <item><description>text after the arguments object has closed (the funnel refuses unparsable arguments);</description></item>
///   <item><description>a value that begins as another kind where the schema declares an object or an array — below
///     the root only: a root sent as a JSON string is a double-encoded object, which is read;</description></item>
///   <item><description>a field name made of code (<see cref="ToolCallArguments.IsCutKey"/>).</description></item>
/// </list>
/// Strings, numbers and booleans are never judged against each other: the tools read <c>"5"</c> and <c>"true"</c>.
/// An MCP tool is not watched: its schema is a third party's, and may declare free-form objects.
/// </para>
/// </remarks>
internal sealed class ArgumentsShapeWatcher(Func<string, JsonElement?> schemaOf)
{
    private readonly Dictionary<int, Call> _calls = [];

    /// <summary>Every call the stream has opened has its arguments object closed — the moment a server sends the end
    /// of the turn at once (0.02 s at most over 348 captured calls).</summary>
    /// <remarks>⚠ A quote left unescaped can close the object EARLY — <c>{"edits":[{"path":…,"old_content":"…($"}]}</c>
    /// — and LM Studio, holding the call complete, then streams nothing of what the model goes on writing: 150 to 337 s
    /// of silence in which no fragment reaches this watcher. Silence after a closed object is that, never a slow server
    /// (<see cref="ClosedThenSilent"/>). An MCP call or a double-encoded root is never "closed" here.</remarks>
    public bool AllClosed => _calls.Count > 0 && _calls.Values.All(c => c.Closed);

    /// <summary>The cause given when the stream stays silent after <see cref="AllClosed"/>.</summary>
    internal static string ClosedThenSilent(TimeSpan silence) =>
        $"its arguments were complete, then nothing more came for {silence.TotalSeconds:0} seconds while the model was "
        + "still writing past the end of the call";

    /// <summary>Feeds one streamed fragment of call <paramref name="index"/>; returns why its arguments can no longer
    /// be read by the tool, or <c>null</c>.</summary>
    public string? Breaks(int index, string? name, string? arguments)
    {
        if (!_calls.TryGetValue(index, out var call) || (!string.IsNullOrEmpty(name) && call.Started))
            _calls[index] = call = new Call();
        if (!string.IsNullOrEmpty(name)) call.Name = name;
        if (string.IsNullOrEmpty(arguments) || call.Finished) return null;

        if (!call.Started)
        {
            call.Started = true;
            if (Mcp.McpTool.IsMcpName(call.Name)) { call.Finished = true; return null; }
            call.Schema = schemaOf(call.Name);
        }
        foreach (var c in arguments)
            if (call.Feed(c) is { } why)
            {
                call.Finished = call.Broken = true;
                return why;
            }
        return null;
    }

    private sealed class Frame
    {
        public bool         IsObject;
        public JsonElement? Schema;
        public string       Label = string.Empty;   // what an element is part of, for the message
        public bool         ExpectKey;
        public bool         ExpectValue;
        public string?      Key;
    }

    private sealed class Call
    {
        public string       Name = string.Empty;
        public bool         Started, Finished;
        public JsonElement? Schema;

        private readonly List<Frame>   _stack = [];
        private readonly StringBuilder _key   = new();
        private bool _inString, _inKey, _escape, _closed;

        /// <summary>The arguments object opened and closed, nothing judged broken.</summary>
        public bool Closed => _closed && !Broken;
        public bool Broken;

        public string? Feed(char c)
        {
            if (_inString)
            {
                if (_escape) { _escape = false; if (_inKey) _key.Append(c); return null; }
                if (c == '\\') { _escape = true; return null; }
                if (c != '"') { if (_inKey) _key.Append(c); return null; }
                _inString = false;
                if (!_inKey) return null;
                _inKey = false;
                var key = _key.ToString();
                _stack[^1].Key = key;
                return ToolCallArguments.IsCutKey(key) ? $"a field was named \"{Short(key)}\"" : null;
            }
            if (char.IsWhiteSpace(c)) return null;
            if (_closed) return "text went on after the arguments object had closed";

            if (_stack.Count == 0)
            {
                // A root that is not an object is a double-encoded object (read by the funnel) or a malformed call
                // (refused by it): either way not this watcher's to judge.
                if (c == '{') _stack.Add(new Frame { IsObject = true, Schema = Schema, ExpectKey = true });
                else Finished = true;
                return null;
            }

            var top = _stack[^1];
            if (top.IsObject)
            {
                if (top.ExpectKey)
                {
                    if (c == '"') { top.ExpectKey = false; _inString = _inKey = true; _key.Clear(); }
                    else if (c == '}') Pop();
                    return null;
                }
                if (c == ':') { top.ExpectValue = true; return null; }
                if (top.ExpectValue)
                {
                    top.ExpectValue = false;
                    return Begin(c, Property(top.Schema, top.Key), top.Key ?? string.Empty, $"'{top.Key}'");
                }
                if (c == ',') top.ExpectKey = true;
                else if (c == '}') Pop();
                return null;
            }

            if (c == ']') { Pop(); return null; }
            if (c == ',') { top.ExpectValue = true; return null; }
            if (top.ExpectValue)
            {
                top.ExpectValue = false;
                return Begin(c, Items(top.Schema), top.Label, $"an element of '{top.Label}'");
            }
            return null;
        }

        private string? Begin(char c, JsonElement? schema, string label, string where)
        {
            var kind = c switch
            {
                '{' => "object", '[' => "array", '"' => "text", 't' or 'f' => "a boolean", 'n' => "null", _ => "a number",
            };
            var expected = TypeOf(schema);
            if (expected is "object" or "array" && kind != expected)
                return $"{where} began as {kind} where the tool reads an {expected}";

            if (c == '{') _stack.Add(new Frame { IsObject = true, Schema = schema, ExpectKey = true });
            else if (c == '[') _stack.Add(new Frame { Schema = schema, Label = label, ExpectValue = true });
            else if (c == '"') _inString = true;
            return null;
        }

        private void Pop()
        {
            _stack.RemoveAt(_stack.Count - 1);
            if (_stack.Count == 0) _closed = true;
        }

        private static string Short(string s) => s.Length <= 60 ? s : s[..60] + "…";
    }

    private static string? TypeOf(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

    private static JsonElement? Property(JsonElement? schema, string? key) =>
        key is not null && schema is { ValueKind: JsonValueKind.Object } s
        && s.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
        && props.TryGetProperty(key, out var p)
            ? p
            : null;

    private static JsonElement? Items(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("items", out var items) ? items : null;
}
