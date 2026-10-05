using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Models;

namespace Inferpal.Services.Agent;

/// <summary>
/// Recovers tool calls that a model emitted as <b>plain text in the message content</b>
/// instead of in the structured <c>tool_calls</c> field of the Ollama response.
/// </summary>
/// <remarks>
/// <para>
/// Several local models (notably Qwen and Hermes-templated checkpoints) sometimes serialise
/// their tool invocation straight into the assistant <c>content</c> string rather than the
/// dedicated <c>tool_calls</c> array — e.g.
/// <c>{"id":"1","name":"fetch_url","arguments":{"url":"https://…"}}</c> or a
/// <c>&lt;tool_call&gt;…&lt;/tool_call&gt;</c> block. When that happens the orchestrator sees no
/// tool calls, prints the raw JSON as the answer, and stops without ever running the tool.
/// </para>
/// <para>
/// This parser detects those payloads and promotes them to real <see cref="ToolCallDto"/>s.
/// Supported shapes:
/// </para>
/// <list type="bullet">
///   <item><description><c>&lt;tool_call&gt;{…}&lt;/tool_call&gt;</c> blocks (one or more) — including a body that stops
///     short of its closing brackets, or is broken with its name readable (<see cref="TryAddMalformedTagCall"/>).</description></item>
///   <item><description>The Qwen/GLM XML shape
///     <c>&lt;tool_call&gt;&lt;function=name&gt;&lt;parameter=key&gt;value&lt;/parameter&gt;…&lt;/function&gt;&lt;/tool_call&gt;</c>.</description></item>
///   <item><description>Meta Muse Glimmer's ATEM shape
///     <c>&lt;atem:function_calls&gt;&lt;atem:invoke name="name"&gt;&lt;atem:parameter name="key"&gt;value&lt;/atem:parameter&gt;…&lt;/atem:invoke&gt;&lt;/atem:function_calls&gt;</c>.</description></item>
///   <item><description>GLM's shape <c>&lt;tool_call&gt;name&lt;arg_key&gt;key&lt;/arg_key&gt;&lt;arg_value&gt;value&lt;/arg_value&gt;&lt;/tool_call&gt;</c>.</description></item>
///   <item><description>Gemma 4's shape <c>&lt;|tool_call&gt;call:name{key:&lt;|"|&gt;value&lt;|"|&gt;}&lt;tool_call|&gt;</c>.</description></item>
///   <item><description>Mistral's shape <c>[TOOL_CALLS]name[ARGS]{json}</c> (Devstral), and the older <c>[TOOL_CALLS][{…}]</c> list.</description></item>
///   <item><description>Cohere's shape <c>&lt;|START_ACTION|&gt;[{"tool_call_id":…,"tool_name":…,"parameters":{…}}]&lt;|END_ACTION|&gt;</c> (North Mini Code).</description></item>
///   <item><description>A bare object <c>{"name":…,"arguments":{…}}</c> (optionally with an <c>id</c>).</description></item>
///   <item><description>An array <c>[{…},{…}]</c> of such objects.</description></item>
///   <item><description>The Ollama-nested shape <c>{"function":{"name":…,"arguments":…}}</c>.</description></item>
///   <item><description><c>arguments</c> as an object, or as a JSON-encoded string; the <c>parameters</c> alias.</description></item>
///   <item><description>Any of the above wrapped in a <c>```json … ```</c> code fence.</description></item>
/// </list>
/// </remarks>
internal static class InlineToolCallParser
{
    private static readonly Regex ToolCallTagRegex =
        new(@"<tool_call>\s*(\{.*?\})\s*</tool_call>", RegexOptions.Singleline | RegexOptions.Compiled, RegexBudget.Default);

    // Qwen/GLM-style XML call: <tool_call><function=NAME>…params…</function></tool_call>.
    // Group 1 is the function name; group 2 is the (possibly empty) parameter block.
    private static readonly Regex FunctionCallXmlRegex =
        new(@"<tool_call>\s*<function=([^>]+?)>(.*?)</function>\s*</tool_call>",
            RegexOptions.Singleline | RegexOptions.Compiled, RegexBudget.Default);

    // One <parameter=KEY>VALUE</parameter> pair inside a function block.
    private static readonly Regex ParameterXmlRegex =
        new(@"<parameter=([^>]+?)>(.*?)</parameter>", RegexOptions.Singleline | RegexOptions.Compiled, RegexBudget.Default);

    // Meta Muse Glimmer's ATEM call: <atem:function_calls><atem:invoke name="NAME">…</atem:invoke></atem:function_calls>.
    // A server that parses the model's format turns it into structured calls; LM Studio does so only when a call is
    // forced (tool_choice "required"), and otherwise streams it as text inside the model's addressed message.
    private static readonly Regex AtemBlockRegex =
        new(@"<atem:function_calls>(.*?)</atem:function_calls>", RegexOptions.Singleline | RegexOptions.Compiled, RegexBudget.Default);

    private static readonly Regex AtemInvokeRegex =
        new(@"<atem:invoke\s+name=""([^""]+)""\s*>(.*?)</atem:invoke>", RegexOptions.Singleline | RegexOptions.Compiled, RegexBudget.Default);

    // One <atem:parameter name="KEY">VALUE</atem:parameter> pair inside an invoke.
    private static readonly Regex AtemParameterRegex =
        new(@"<atem:parameter\s+name=""([^""]+)""\s*>(.*?)</atem:parameter>", RegexOptions.Singleline | RegexOptions.Compiled, RegexBudget.Default);

    // GLM-4.5/4.6/4.7: <tool_call>NAME<arg_key>K</arg_key><arg_value>V</arg_value>…</tool_call> — the name bare (a newline
    // after it on 4.5 and 4.6, none on 4.7), string values raw, other types JSON.
    private static readonly Regex GlmCallRegex =
        new(@"<tool_call>\s*([^\s<{][^<]*?)\s*((?:<arg_key>.*?</arg_key>\s*<arg_value>.*?</arg_value>\s*)*)</tool_call>",
            RegexOptions.Singleline | RegexOptions.Compiled, RegexBudget.Default);

    // One <arg_key>KEY</arg_key><arg_value>VALUE</arg_value> pair inside a GLM call.
    private static readonly Regex GlmArgRegex =
        new(@"<arg_key>(.*?)</arg_key>\s*<arg_value>(.*?)</arg_value>", RegexOptions.Singleline | RegexOptions.Compiled, RegexBudget.Default);

    // Gemma 4: <|tool_call>call:NAME{…}<tool_call|>, the arguments in Gemma's own syntax (GemmaCallArguments).
    private static readonly Regex GemmaCallRegex =
        new(@"<\|tool_call>\s*call:([^\s{]+?)\s*(\{.*?\})\s*<tool_call\|>", RegexOptions.Singleline | RegexOptions.Compiled, RegexBudget.Default);

    // Cohere (North Mini Code, Command): <|START_ACTION|>[{"tool_call_id":…,"tool_name":…,"parameters":{…}},…]<|END_ACTION|>,
    // written after the reasoning's <|END_THINKING|>. LM Studio leaves it as text for North Mini Code: unread, no call runs.
    private static readonly Regex CohereActionRegex =
        new(@"<\|START_ACTION\|>\s*(\[.*?\]|\{.*?\})\s*<\|END_ACTION\|>", RegexOptions.Singleline | RegexOptions.Compiled, RegexBudget.Default);

    // Mistral (Devstral, tokenizer v11 and later): [TOOL_CALLS]name[ARGS]{json}, repeated for parallel calls.
    private const string MistralCallsToken = "[TOOL_CALLS]";
    private const string MistralArgsToken  = "[ARGS]";

    private static readonly Regex CodeFenceRegex =
        new(@"^```(?:json)?\s*\n?(.*?)\n?```$", RegexOptions.Singleline | RegexOptions.Compiled, RegexBudget.Default);

    /// <summary>
    /// Attempts to extract tool calls from text content the model produced.
    /// </summary>
    /// <param name="content">The raw assistant content string.</param>
    /// <param name="isKnownTool">
    /// When provided, gates the <b>bare-JSON</b> shape (2) only: a whole-content JSON object is
    /// promoted to a tool call solely when its <c>name</c> is a registered tool. Without the gate,
    /// asking the model for "a JSON of a person" turned <c>{"name":"Alice","age":30}</c> into a
    /// call of the tool "Alice" and DESTROYED the legitimate answer. The
    /// explicit <c>&lt;tool_call&gt;</c> shapes stay ungated: their intent is unambiguous, and an
    /// unknown name there should keep flowing to the registry's "Unknown tool" feedback, which is
    /// what lets the model correct a typo.
    /// </param>
    /// <returns>
    /// A tuple of the recovered calls (<c>null</c> when none were found) and the content with the
    /// consumed tool-call JSON stripped out (so it is not also shown to the user as text).
    /// When nothing is recovered, the original content is returned unchanged.
    /// </returns>
    public static (List<ToolCallDto>? Calls, string Cleaned) TryParse(
        string? content, Func<string, bool>? isKnownTool = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, content ?? string.Empty);
        return ParseNonEmpty(content, isKnownTool);
    }

    /// <summary>
    /// The tool calls a turn wrote as text in its CONTENT, the stream having carried no structured one — the decision
    /// both chat clients make, with the names of the tools the request <paramref name="offered"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ With no tool offered — an inline edit, <c>/doc</c>, a summary, a commit message, a synthesis — text that SHOWS a
    /// call is content: a source file holding a tool-call literal, an answer explaining a format. Read as a call, it is
    /// cut out of the reply, and an inline edit drops those lines from the user's code. A reply that is nothing but a
    /// call is still read as one: asked for an answer, the model has written none, and every reader of such a reply
    /// refuses an empty one.
    /// </remarks>
    public static (List<ToolCallDto>? Calls, string Cleaned) FromContent(string content, IReadOnlyCollection<string> offered)
    {
        var (calls, cleaned) = TryParse(content, offered.Contains);
        if (calls is not { Count: > 0 }) return (null, content);
        if (offered.Count == 0 && MarkdownParser.HasPrintableText(MarkdownParser.StripThinkTags(cleaned)))
            return (null, content);
        return (calls, cleaned);
    }

    private static (List<ToolCallDto>? Calls, string Cleaned) ParseNonEmpty(string content, Func<string, bool>? isKnownTool)
    {

        var calls = new List<ToolCallDto>();

        // (1) <tool_call>{…}</tool_call> blocks — may appear alongside ordinary prose.
        var cleaned   = content;
        var tagMatched = false;
        foreach (Match m in ToolCallTagRegex.Matches(content))
        {
            if (TryAddFromJson(m.Groups[1].Value, calls) || TryAddMalformedTagCall(m.Groups[1].Value, calls))
            {
                cleaned    = cleaned.Replace(m.Value, string.Empty);
                tagMatched = true;
            }
        }
        if (tagMatched && calls.Count > 0)
            return (calls, cleaned.Trim());

        // (1b) Qwen/GLM XML function-call shape:
        //   <tool_call><function=name><parameter=key>value</parameter></function></tool_call>
        // Reasoning models (e.g. Qwen3 on LM Studio) emit this when forced to call a tool.
        var xmlMatched = false;
        foreach (Match m in FunctionCallXmlRegex.Matches(content))
        {
            if (TryAddFromFunctionXml(m.Groups[1].Value, m.Groups[2].Value, calls))
            {
                cleaned    = cleaned.Replace(m.Value, string.Empty);
                xmlMatched = true;
            }
        }
        if (xmlMatched && calls.Count > 0)
            return (calls, cleaned.Trim());

        // (1c) Meta Muse Glimmer's ATEM shape:
        //   <atem:function_calls><atem:invoke name="name"><atem:parameter name="key">value</atem:parameter></atem:invoke></atem:function_calls>
        var atemMatched = false;
        foreach (Match block in AtemBlockRegex.Matches(content))
        {
            var before = calls.Count;
            foreach (Match invoke in AtemInvokeRegex.Matches(block.Groups[1].Value))
                TryAddFromXmlParameters(invoke.Groups[1].Value, invoke.Groups[2].Value, AtemParameterRegex, calls);
            if (calls.Count > before)
            {
                cleaned     = cleaned.Replace(block.Value, string.Empty);
                atemMatched = true;
            }
        }
        if (atemMatched && calls.Count > 0)
            return (calls, cleaned.Trim());

        // (1d) GLM's shape: <tool_call>name<arg_key>key</arg_key><arg_value>value</arg_value></tool_call>
        var glmMatched = false;
        foreach (Match m in GlmCallRegex.Matches(content))
        {
            if (TryAddFromXmlParameters(m.Groups[1].Value, m.Groups[2].Value, GlmArgRegex, calls))
            {
                cleaned    = cleaned.Replace(m.Value, string.Empty);
                glmMatched = true;
            }
        }
        if (glmMatched && calls.Count > 0)
            return (calls, cleaned.Trim());

        // (1e) Gemma 4's shape: <|tool_call>call:name{key:<|"|>value<|"|>}<tool_call|>
        var gemmaMatched = false;
        foreach (Match m in GemmaCallRegex.Matches(content))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Length == 0) continue;
            var json = GemmaCallArguments.ToJson(m.Groups[2].Value);
            calls.Add(json is null
                ? new ToolCallDto(new ToolCallFunction(name, EmptyObject()) { UnparsedArguments = m.Groups[2].Value })
                : new ToolCallDto(new ToolCallFunction(name, ParseObject(json))));
            cleaned      = cleaned.Replace(m.Value, string.Empty);
            gemmaMatched = true;
        }
        if (gemmaMatched)
            return (calls, cleaned.Trim());

        // (1f) Mistral's shape: [TOOL_CALLS]name[ARGS]{json}
        if (TryAddMistralCalls(content, calls, ref cleaned))
            return (calls, cleaned.Trim());

        // (1g) Cohere's shape: <|START_ACTION|>[{"tool_name":…,"parameters":{…}}]<|END_ACTION|>
        var cohereMatched = false;
        foreach (Match m in CohereActionRegex.Matches(content))
        {
            if (TryAddFromJson(m.Groups[1].Value, calls))
            {
                cleaned       = cleaned.Replace(m.Value, string.Empty);
                cohereMatched = true;
            }
        }
        if (cohereMatched && calls.Count > 0)
            return (calls, cleaned.Trim());

        // (2) The whole content is a JSON payload (optionally fenced in ```json … ```).
        var payload = StripCodeFence(content.Trim());
        if ((payload.StartsWith('{') || payload.StartsWith('['))
            && TryAddFromJson(payload, calls)
            && calls.Count > 0
            // All-or-nothing gate: a payload with ANY unrecognised name is an answer, not a call.
            && (isKnownTool is null || calls.All(c => isKnownTool(c.Function.Name))))
        {
            return (calls, string.Empty);
        }

        return (null, content);
    }

    /// <summary>
    /// A <c>&lt;tool_call&gt;</c> body that is not valid JSON: still the call the model meant to make.
    /// </summary>
    /// <remarks>
    /// ⚠ Rejected, the block stays in the content and becomes the final answer: the run ends on raw JSON, nothing
    /// executed, and the model never learns its call was malformed (Gemma 4 under <c>PromptedTools</c> stops one
    /// closing bracket short). Two cases, nothing guessed in either:
    /// <list type="bullet">
    ///   <item><description>Unfinished — every string closed, brackets still open at the end: the closers are appended
    ///     and the call is the one the model wrote.</description></item>
    ///   <item><description>Broken elsewhere, the name written first: an unreadable call, which the funnel
    ///     (<c>AgentOrchestrator.ExecuteToolSafeAsync</c>) refuses with the cause, so the model can resend it.</description></item>
    /// </list>
    /// A body with no readable name names no call and stays text.
    /// </remarks>
    private static bool TryAddMalformedTagCall(string body, List<ToolCallDto> calls)
    {
        if (Unfinished(body) is { } completed && TryAddFromJson(completed, calls))
            return true;

        var name = LeadingNameRegex.Match(body);
        if (!name.Success) return false;
        calls.Add(new ToolCallDto(new ToolCallFunction(name.Groups[1].Value, EmptyObject()) { UnparsedArguments = body }));
        return true;
    }

    // The name written as the first property — the shape every model writes; a "name" further in may be an argument's.
    private static readonly Regex LeadingNameRegex =
        new(@"^\{\s*""name""\s*:\s*""([A-Za-z0-9_.\-]+)""", RegexOptions.Compiled, RegexBudget.Default);

    /// <summary><paramref name="json"/> with the closers of the brackets it leaves open appended; <c>null</c> when it
    /// is not exactly that — a string still open, a closer that does not match, or nothing left open.</summary>
    private static string? Unfinished(string json)
    {
        var open     = new Stack<char>();
        var inString = false;
        for (var k = 0; k < json.Length; k++)
        {
            var c = json[k];
            if (inString)
            {
                if (c == '\\') k++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c is '{' or '[') open.Push(c == '{' ? '}' : ']');
            else if (c is '}' or ']' && (open.Count == 0 || open.Pop() != c)) return null;
        }
        return inString || open.Count == 0 ? null : json + new string(open.ToArray());   // innermost first
    }

    /// <summary>Parses a JSON object — or array of objects — appending any valid tool calls.</summary>
    private static bool TryAddFromJson(string json, List<ToolCallDto> calls)
    {
        JsonDocument doc;
        try   { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var any = false;
                foreach (var el in root.EnumerateArray())
                    any |= TryAddObject(el, calls);
                return any;
            }
            return TryAddObject(root, calls);
        }
    }

    private static bool TryAddObject(JsonElement obj, List<ToolCallDto> calls)
    {
        if (obj.ValueKind != JsonValueKind.Object) return false;

        // Some models nest the call under "function": { "name":…, "arguments":… }.
        if (obj.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            obj = fn;

        // Cohere names the tool "tool_name".
        if ((!obj.TryGetProperty("name", out var nameEl) && !obj.TryGetProperty("tool_name", out nameEl))
            || nameEl.ValueKind != JsonValueKind.String)
            return false;

        var name = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(name)) return false;

        // Arguments may live under "arguments" or "parameters"; as an object or a JSON string.
        if (TryGetArgs(obj, "arguments", out var argsEl) || TryGetArgs(obj, "parameters", out argsEl))
        {
            calls.Add(new ToolCallDto(new ToolCallFunction(name!, argsEl)));
            return true;
        }

        // ⚠ <b>UNREADABLE arguments are not ABSENT arguments.</b> This site fell back to `{}` in
        // both cases, so a `run_tests` whose filter the model wrote as raw text — the commonest shape
        // from small models — became a `run_tests` with no filter, that is, the WHOLE suite. The rule
        // is written in `OpenAiCompatibleClient.ParseArguments`, with that exact example, and the
        // guard that refuses to execute (`ExecuteToolSafeAsync`) already existed: it was only waiting
        // for this path to mark its calls.
        calls.Add(new ToolCallDto(
            new ToolCallFunction(name!, EmptyObject()) { UnparsedArguments = RawArguments(obj) }));
        return true;
    }

    /// <summary>
    /// The arguments text the model wrote when it did not parse into an object — <c>null</c> when it
    /// wrote none at all, which is a legitimate call without arguments.
    /// </summary>
    /// <remarks>
    /// ⚠ Same contract as <c>OpenAiCompatibleClient.ParseArguments</c>, deliberately: an absent
    /// property, an explicit <c>null</c> and an empty string all mean "no arguments" on the
    /// structured path, and two readers of the same model output must not disagree about which
    /// calls are runnable.
    /// </remarks>
    private static string? RawArguments(JsonElement obj)
    {
        foreach (var prop in (string[])["arguments", "parameters"])
        {
            if (!obj.TryGetProperty(prop, out var el) || el.ValueKind == JsonValueKind.Null) continue;
            var raw = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
            if (!string.IsNullOrWhiteSpace(raw) && raw.Trim() != "null") return raw;
        }
        return null;
    }

    /// <summary>Builds a tool call from the Qwen/GLM XML shape: a function name plus a block of
    /// <c>&lt;parameter=key&gt;value&lt;/parameter&gt;</c> pairs, assembled into a JSON arguments object.</summary>
    private static bool TryAddFromFunctionXml(string name, string paramsBlock, List<ToolCallDto> calls) =>
        TryAddFromXmlParameters(name, paramsBlock, ParameterXmlRegex, calls);

    /// <summary>A call from a name and a block of key/value parameter elements matched by <paramref name="parameter"/>
    /// (group 1 the key, group 2 the value) — the shape the Qwen/GLM XML and the ATEM calls share.</summary>
    private static bool TryAddFromXmlParameters(string name, string paramsBlock, Regex parameter, List<ToolCallDto> calls)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name)) return false;

        var sb    = new System.Text.StringBuilder("{");
        var first = true;
        foreach (Match pm in parameter.Matches(paramsBlock))
        {
            var key = pm.Groups[1].Value.Trim();
            if (key.Length == 0) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializer.Serialize(key)).Append(':')
              .Append(EncodeXmlValue(pm.Groups[2].Value.Trim()));
        }
        sb.Append('}');

        JsonElement args;
        try   { args = JsonDocument.Parse(sb.ToString()).RootElement.Clone(); }
        catch (JsonException) { args = EmptyObject(); }

        calls.Add(new ToolCallDto(new ToolCallFunction(name, args)));
        return true;
    }

    /// <summary>Encodes an XML parameter value as JSON: kept verbatim when it already is a valid JSON
    /// scalar/object/array, otherwise quoted as a JSON string.</summary>
    private static string EncodeXmlValue(string raw)
    {
        if (raw.Length > 0)
        {
            var c = raw[0];
            if (c is '{' or '[' or '"' or '-' || char.IsDigit(c) || raw is "true" or "false" or "null")
            {
                try { using var _ = JsonDocument.Parse(raw); return raw; }
                catch (JsonException) { /* not valid JSON → treat as a string below */ }
            }
        }
        return JsonSerializer.Serialize(raw);
    }

    private static bool TryGetArgs(JsonElement obj, string prop, out JsonElement args)
    {
        args = default;
        if (!obj.TryGetProperty(prop, out var raw)) return false;

        if (raw.ValueKind == JsonValueKind.Object)
        {
            args = raw.Clone(); // Clone so it survives the owning JsonDocument's disposal.
            return true;
        }

        // Arguments serialised as a JSON string, e.g. "{\"url\":\"…\"}".
        if (raw.ValueKind == JsonValueKind.String)
        {
            var s = raw.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                try
                {
                    using var inner = JsonDocument.Parse(s);
                    if (inner.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        args = inner.RootElement.Clone();
                        return true;
                    }
                }
                catch (JsonException) { /* fall through */ }
            }
        }
        return false;
    }

    /// <summary>
    /// Mistral's calls, <c>[TOOL_CALLS]name[ARGS]{json}</c> (and the older <c>[TOOL_CALLS][{"name":…}]</c> list); each
    /// one read is removed from <paramref name="cleaned"/>.
    /// </summary>
    private static bool TryAddMistralCalls(string content, List<ToolCallDto> calls, ref string cleaned)
    {
        var found = false;
        var i     = content.IndexOf(MistralCallsToken, StringComparison.Ordinal);
        while (i >= 0)
        {
            var p = SkipBlanks(content, i + MistralCallsToken.Length);
            int end;
            if (p < content.Length && content[p] == '[')
            {
                end = ClosingBracket(content, p);
                if (end < 0 || !TryAddFromJson(content[p..(end + 1)], calls)) break;
            }
            else
            {
                var args = content.IndexOf(MistralArgsToken, p, StringComparison.Ordinal);
                if (args < 0) break;
                var name = content[p..args].Trim();
                var q    = SkipBlanks(content, args + MistralArgsToken.Length);
                end = q < content.Length && content[q] == '{' ? ClosingBracket(content, q) : -1;
                if (name.Length == 0 || name.Any(char.IsWhiteSpace) || end < 0) break;
                var raw = content[q..(end + 1)];
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    calls.Add(new ToolCallDto(new ToolCallFunction(name, doc.RootElement.Clone())));
                }
                catch (JsonException)
                {
                    calls.Add(new ToolCallDto(new ToolCallFunction(name, EmptyObject()) { UnparsedArguments = raw }));
                }
            }
            cleaned = cleaned.Replace(content[i..(end + 1)], string.Empty);
            found   = true;
            i       = content.IndexOf(MistralCallsToken, end + 1, StringComparison.Ordinal);
        }
        return found;
    }

    /// <summary>The index of the bracket that closes the one at <paramref name="open"/>, strings and escapes respected;
    /// -1 when it never closes.</summary>
    private static int ClosingBracket(string s, int open)
    {
        var depth    = 0;
        var inString = false;
        for (var k = open; k < s.Length; k++)
        {
            var c = s[k];
            if (inString)
            {
                if (c == '\\') k++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']' && --depth == 0) return k;
        }
        return -1;
    }

    private static int SkipBlanks(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    private static JsonElement ParseObject(string json)
    {
        using var d = JsonDocument.Parse(json);
        return d.RootElement.Clone();
    }

    private static JsonElement EmptyObject()
    {
        using var d = JsonDocument.Parse("{}");
        return d.RootElement.Clone();
    }

    private static string StripCodeFence(string s)
    {
        var m = CodeFenceRegex.Match(s);
        return m.Success ? m.Groups[1].Value.Trim() : s;
    }
}
