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
///   <item><description><c>&lt;tool_call&gt;{…}&lt;/tool_call&gt;</c> blocks (one or more).</description></item>
///   <item><description>The Qwen/GLM XML shape
///     <c>&lt;tool_call&gt;&lt;function=name&gt;&lt;parameter=key&gt;value&lt;/parameter&gt;…&lt;/function&gt;&lt;/tool_call&gt;</c>.</description></item>
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
        new(@"<tool_call>\s*(\{.*?\})\s*</tool_call>", RegexOptions.Singleline | RegexOptions.Compiled);

    // Qwen/GLM-style XML call: <tool_call><function=NAME>…params…</function></tool_call>.
    // Group 1 is the function name; group 2 is the (possibly empty) parameter block.
    private static readonly Regex FunctionCallXmlRegex =
        new(@"<tool_call>\s*<function=([^>]+?)>(.*?)</function>\s*</tool_call>",
            RegexOptions.Singleline | RegexOptions.Compiled);

    // One <parameter=KEY>VALUE</parameter> pair inside a function block.
    private static readonly Regex ParameterXmlRegex =
        new(@"<parameter=([^>]+?)>(.*?)</parameter>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CodeFenceRegex =
        new(@"^```(?:json)?\s*\n?(.*?)\n?```$", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Attempts to extract tool calls from text content the model produced.
    /// </summary>
    /// <param name="content">The raw assistant content string.</param>
    /// <returns>
    /// A tuple of the recovered calls (<c>null</c> when none were found) and the content with the
    /// consumed tool-call JSON stripped out (so it is not also shown to the user as text).
    /// When nothing is recovered, the original content is returned unchanged.
    /// </returns>
    public static (List<ToolCallDto>? Calls, string Cleaned) TryParse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, content ?? string.Empty);

        var calls = new List<ToolCallDto>();

        // (1) <tool_call>{…}</tool_call> blocks — may appear alongside ordinary prose.
        var cleaned   = content;
        var tagMatched = false;
        foreach (Match m in ToolCallTagRegex.Matches(content))
        {
            if (TryAddFromJson(m.Groups[1].Value, calls))
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

        // (2) The whole content is a JSON payload (optionally fenced in ```json … ```).
        var payload = StripCodeFence(content.Trim());
        if ((payload.StartsWith('{') || payload.StartsWith('['))
            && TryAddFromJson(payload, calls)
            && calls.Count > 0)
        {
            return (calls, string.Empty);
        }

        return (null, content);
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

        if (!obj.TryGetProperty("name", out var nameEl)
            || nameEl.ValueKind != JsonValueKind.String)
            return false;

        var name = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(name)) return false;

        // Arguments may live under "arguments" or "parameters"; as an object or a JSON string.
        if (!TryGetArgs(obj, "arguments", out var argsEl) && !TryGetArgs(obj, "parameters", out argsEl))
            argsEl = EmptyObject();

        calls.Add(new ToolCallDto(new ToolCallFunction(name!, argsEl)));
        return true;
    }

    /// <summary>Builds a tool call from the Qwen/GLM XML shape: a function name plus a block of
    /// <c>&lt;parameter=key&gt;value&lt;/parameter&gt;</c> pairs, assembled into a JSON arguments object.</summary>
    private static bool TryAddFromFunctionXml(string name, string paramsBlock, List<ToolCallDto> calls)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name)) return false;

        var sb    = new System.Text.StringBuilder("{");
        var first = true;
        foreach (Match pm in ParameterXmlRegex.Matches(paramsBlock))
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
