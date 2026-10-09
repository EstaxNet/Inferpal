using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inferpal.Services.Mcp;

/// <summary>
/// Inlines the LOCAL <c>$ref</c>s of an MCP tool's input schema (<c>#/$defs/…</c>, <c>#/definitions/…</c>) before the
/// schema is offered to the model.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Ollama keeps, of a parameter's schema, only <c>type</c>, <c>description</c>, <c>enum</c>, <c>items</c>,
/// <c>properties</c>, <c>required</c> and <c>anyOf</c>: a parameter written as <c>{"$ref": "#/$defs/Address"}</c>
/// reaches the model with no type at all. That is how the Python SDK writes every structured argument (a nested
/// Pydantic model), and JSON Schema 2020-12 — the dialect MCP tool schemas default to — makes it ordinary.
/// </para>
/// <para>
/// A reference that is recursive, external (never fetched: a schema is the server's text, not an address to follow), or
/// past the size bound stays as written. The schema read for <c>x-mcp-header</c> is the original one: there, a
/// <c>$ref</c> on the path makes the annotation invalid, and inlining would hide that.
/// </para>
/// </remarks>
internal static class McpSchemaRefs
{
    /// <summary>Past this many nodes the schema is offered as the server wrote it: expansion can grow it exponentially.</summary>
    private const int MaxNodes = 20_000;

    private const int MaxDepth = 64;

    /// <summary>Keywords whose value is data, not a schema: a <c>$ref</c> inside one is text and stays.</summary>
    private static readonly HashSet<string> DataKeywords = new(StringComparer.Ordinal)
    {
        "const", "default", "enum", "examples",
    };

    public static JsonElement Inline(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.GetRawText().Contains("\"$ref\"", StringComparison.Ordinal))
            return schema;

        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root) return schema;
        var budget = MaxNodes;
        var expanded = Expand(root, root, [], 0, ref budget) as JsonObject;
        if (expanded is null || budget < 0) return schema;

        // The definitions are dead weight in the context once nothing points at them any more.
        if (!HasLocalRef(expanded))
        {
            expanded.Remove("$defs");
            expanded.Remove("definitions");
        }
        using var doc = JsonDocument.Parse(expanded.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static JsonNode? Expand(JsonNode? node, JsonObject root, HashSet<string> active, int depth, ref int budget)
    {
        if (--budget < 0 || depth > MaxDepth) return node?.DeepClone();
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj["$ref"] is JsonValue r && r.TryGetValue<string>(out var reference) && reference.StartsWith('#')
                    && !active.Contains(reference) && Resolve(root, reference) is JsonObject target)
                {
                    active.Add(reference);
                    var inlined = Expand(target, root, active, depth + 1, ref budget) as JsonObject ?? new JsonObject();
                    active.Remove(reference);
                    // Keywords beside the $ref (2020-12 allows them: a description, a default) apply too, and say more.
                    foreach (var (key, value) in obj)
                        if (key != "$ref")
                            inlined[key] = Copy(key, value, root, active, depth, ref budget);
                    return inlined;
                }
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                    copy[key] = Copy(key, value, root, active, depth, ref budget);
                return copy;
            }
            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (var item in array)
                    copy.Add(Expand(item, root, active, depth + 1, ref budget));
                return copy;
            }
            default:
                return node?.DeepClone();
        }
    }

    private static JsonNode? Copy(string key, JsonNode? value, JsonObject root, HashSet<string> active, int depth, ref int budget) =>
        DataKeywords.Contains(key) ? value?.DeepClone() : Expand(value, root, active, depth + 1, ref budget);

    /// <summary>The node a local JSON pointer (<c>#/a/b</c>) designates in <paramref name="root"/>, or <c>null</c>.</summary>
    private static JsonNode? Resolve(JsonObject root, string reference)
    {
        JsonNode? node = root;
        foreach (var raw in reference[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = Uri.UnescapeDataString(raw).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                JsonObject o => o.TryGetPropertyValue(token, out var next) ? next : null,
                JsonArray a when int.TryParse(token, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    private static bool HasLocalRef(JsonNode? node) => node switch
    {
        JsonObject o => (o["$ref"] is JsonValue r && r.TryGetValue<string>(out var s) && s.StartsWith('#'))
                        || o.Any(kv => !DataKeywords.Contains(kv.Key) && HasLocalRef(kv.Value)),
        JsonArray a => a.Any(HasLocalRef),
        _ => false,
    };
}
