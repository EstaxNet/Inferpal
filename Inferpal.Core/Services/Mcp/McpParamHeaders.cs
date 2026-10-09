using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inferpal.Services.Mcp;

/// <summary>
/// The tool parameters a server mirrors into HTTP headers (<c>x-mcp-header</c>, revision 2026-07-28): which ones, read
/// from the tool's input schema, and the value encoding every MCP header uses (<c>Mcp-Name</c> included).
/// </summary>
/// <remarks>
/// ⚠ The client MUST send these headers on Streamable HTTP: a server validates them against the body and refuses a call
/// without them (<c>HeaderMismatch</c>). And it MUST leave out a tool whose annotations break the rules — one malformed
/// definition costs that tool, not the server.
/// </remarks>
internal static class McpParamHeaders
{
    /// <summary>One mirrored parameter: <c>Mcp-Param-{Name}</c> carries the argument at <paramref name="Path"/>.</summary>
    internal sealed record Annotation(string Name, IReadOnlyList<string> Path);

    private const string Keyword = "x-mcp-header";

    /// <summary>The keywords whose value is a subschema (or a map, or a list, of subschemas) — where an annotation can
    /// hide. Values of any other keyword are data (<c>const</c>, <c>default</c>, <c>examples</c>…) and never walked.</summary>
    private static readonly HashSet<string> SubschemaKeywords = new(StringComparer.Ordinal)
    {
        "items", "prefixItems", "additionalProperties", "patternProperties", "anyOf", "oneOf", "allOf", "not",
        "if", "then", "else", "$defs", "definitions", "dependentSchemas", "contains", "propertyNames",
        "unevaluatedItems", "unevaluatedProperties", "additionalItems",
    };

    /// <summary>
    /// The annotations of an input schema, or why the tool must be left out.
    /// </summary>
    /// <remarks>
    /// An annotation is valid only on a property reached from the root through <c>properties</c> keys alone — never
    /// through <c>items</c>, a composition keyword, a condition or a <c>$ref</c> — with a <c>string</c>, <c>integer</c>
    /// or <c>boolean</c> type, and a name that is an HTTP token, unique without regard to case.
    /// </remarks>
    public static (IReadOnlyList<Annotation> Annotations, string? Rejection) Read(JsonElement schema)
    {
        var found = new List<Annotation>();
        string? rejection = null;
        Walk(schema, [], reachable: true, isRoot: true, found, ref rejection);
        if (rejection is null)
        {
            var duplicate = found.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null) rejection = $"the header name '{duplicate.Key}' is used twice";
        }
        return rejection is null ? (found, null) : ([], rejection);
    }

    private static void Walk(JsonElement node, List<string> path, bool reachable, bool isRoot,
                             List<Annotation> found, ref string? rejection)
    {
        if (rejection is not null || node.ValueKind != JsonValueKind.Object) return;

        if (node.TryGetProperty(Keyword, out var annotation))
        {
            var where = path.Count == 0 ? "the schema root" : $"'{string.Join(".", path)}'";
            if (isRoot || !reachable)
            {
                rejection = $"{Keyword} on {where}, which is not a parameter reached through 'properties' alone";
                return;
            }
            if (annotation.ValueKind != JsonValueKind.String || annotation.GetString() is not { Length: > 0 } name)
            {
                rejection = $"{Keyword} on {where} is empty or not a string";
                return;
            }
            if (!name.All(IsTokenChar))
            {
                rejection = $"{Keyword} '{name}' on {where} is not an HTTP header name";
                return;
            }
            var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (type is not ("string" or "integer" or "boolean"))
            {
                rejection = $"{Keyword} '{name}' on {where}, whose type is {(type is null ? "not a single primitive" : $"'{type}'")} (string, integer or boolean only)";
                return;
            }
            found.Add(new Annotation(name, [.. path]));
        }

        foreach (var member in node.EnumerateObject())
        {
            if (member.Name == "properties" && member.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in member.Value.EnumerateObject())
                {
                    path.Add(property.Name);
                    Walk(property.Value, path, reachable, isRoot: false, found, ref rejection);
                    path.RemoveAt(path.Count - 1);
                }
            }
            else if (SubschemaKeywords.Contains(member.Name))
            {
                // Below one of these, nothing is statically reachable any more.
                if (member.Value.ValueKind == JsonValueKind.Array)
                    foreach (var item in member.Value.EnumerateArray())
                        Walk(item, path, reachable: false, isRoot: false, found, ref rejection);
                else if (member.Name is "$defs" or "definitions" or "patternProperties" or "dependentSchemas"
                         && member.Value.ValueKind == JsonValueKind.Object)
                    foreach (var entry in member.Value.EnumerateObject())
                        Walk(entry.Value, path, reachable: false, isRoot: false, found, ref rejection);
                else
                    Walk(member.Value, path, reachable: false, isRoot: false, found, ref rejection);
            }
        }
    }

    /// <summary><c>tchar</c> of RFC 9110 §5.6.2.</summary>
    private static bool IsTokenChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    /// <summary>
    /// The header value of one mirrored argument, before encoding: <c>null</c> when the argument is absent or
    /// <c>null</c> (the header is then omitted), or not of a primitive kind.
    /// </summary>
    public static string? ValueFor(JsonNode? arguments, IReadOnlyList<string> path)
    {
        var node = arguments;
        foreach (var key in path)
            node = node is JsonObject obj && obj.TryGetPropertyValue(key, out var next) ? next : null;
        if (node is not JsonValue value) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            JsonValueKind.True   => "true",
            JsonValueKind.False  => "false",
            _                    => null,
        };
    }

    /// <summary>
    /// A header value as the specification has it written: as-is when it is plain visible ASCII, otherwise — non-ASCII,
    /// a control character, leading or trailing white space, or a plain value shaped like the sentinel —
    /// <c>=?base64?…?=</c> of its UTF-8 bytes.
    /// </summary>
    public static string Encode(string value)
    {
        var plain = value.Length > 0
                    && value.All(c => c is >= ' ' and <= '~' or '\t')
                    && value[0] is not (' ' or '\t') && value[^1] is not (' ' or '\t')
                    && !(value.StartsWith("=?base64?", StringComparison.Ordinal) && value.EndsWith("?=", StringComparison.Ordinal));
        return plain || value.Length == 0 ? value : $"=?base64?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";
    }
}
