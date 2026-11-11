using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inferpal.Config;

/// <summary>How a setting that holds a collection in one string splits into elements.</summary>
internal enum CollectionShape
{
    /// <summary>One element per non-blank line (pinned files, permission rules).</summary>
    Lines,
    /// <summary>One <c>name=value</c> entry per line, identified by its name (command templates, custom tools): a line
    /// written <c>/review = …</c> in the file and <c>/review=…</c> by the settings window is one entry, unchanged — read as
    /// two lines, the name would end up twice, the other editor's first. A line that is no entry is its own text.</summary>
    NameValueLines,
    /// <summary>A JSON object keyed by name, bare or under <c>mcpServers</c> (MCP servers).</summary>
    JsonObjectByName,
    /// <summary>A JSON array of objects identified by their <c>id</c> (documentation sources).</summary>
    JsonArrayById,
}

/// <summary>
/// Marks a setting that holds a COLLECTION: <see cref="InferpalConfig.Save"/> merges its elements with what the other
/// editor wrote since this copy was read, never the whole key.
/// </summary>
/// <remarks>
/// ⚠ Both editors (and every window of each) keep their own copy of <c>config.json</c>, and a save lays over the file
/// only the keys its copy changed. A key holding a list is ONE key: a pin added here wrote this copy's whole list, and
/// the file lost the file the other window pinned — likewise a docs source, an MCP server (whose sign-in then went
/// with it), a deny rule.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class CollectionSettingAttribute(CollectionShape shape) : Attribute
{
    public CollectionShape Shape { get; } = shape;
}

/// <summary>Three-way merge of a collection setting, element by element.</summary>
internal static class CollectionMerge
{
    private readonly record struct Element(string Key, string Text);

    /// <summary>
    /// <paramref name="mine"/> with what the file changed since <paramref name="baseline"/> applied to it: an element
    /// the file added is kept (after its neighbour there), one it removed or changed — and this copy left as it was —
    /// follows the file. What this copy added, changed or removed wins. <c>null</c> when a side cannot be split (the
    /// caller then writes this copy's value whole, as before).
    /// </summary>
    internal static string? Merge(CollectionShape shape, string baseline, string onDisk, string mine)
    {
        if (Split(shape, baseline, out var b, out _) is false
            || Split(shape, onDisk, out var d, out _) is false
            || Split(shape, mine, out var m, out var wrapped) is false)
            return null;

        var before = ToMap(b);
        var mineKeys = m.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var disk = ToMap(d);

        var result = new List<Element>();
        foreach (var e in m)
        {
            if (before.TryGetValue(e.Key, out var was) && Same(shape, was, e.Text))
            {
                // This copy left the element as it was: the file's version stands — changed, or gone.
                if (disk.TryGetValue(e.Key, out var now)) result.Add(new Element(e.Key, now));
                continue;
            }
            result.Add(e);   // this copy added or changed it
        }

        // The elements the file added, each after its nearest neighbour in the file's order.
        for (var i = 0; i < d.Count; i++)
        {
            var e = d[i];
            if (before.ContainsKey(e.Key) || mineKeys.Contains(e.Key)) continue;
            var at = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var neighbour = result.FindIndex(r => r.Key == d[j].Key);
                if (neighbour >= 0) { at = neighbour + 1; break; }
            }
            result.Insert(at, e);
        }

        return Join(shape, result, mine, wrapped);
    }

    /// <summary>An element this copy left as it was: the same text, or for an entry, the same entry however it is
    /// written.</summary>
    private static bool Same(CollectionShape shape, string was, string now) =>
        was == now
        || shape == CollectionShape.NameValueLines && NameValue(was.Trim()) is { } a && a == NameValue(now.Trim());

    /// <summary>One trimmed line of a <see cref="CollectionShape.NameValueLines"/> setting as an entry: off when it starts
    /// with <c>#</c>; <c>null</c> when it is none (no <c>=</c>, an empty side). The one reader of that format.</summary>
    internal static (bool Enabled, string Name, string Value)? NameValue(string line)
    {
        var enabled = !line.StartsWith('#');
        var body    = enabled ? line : line.TrimStart('#').Trim();
        var eq      = body.IndexOf('=');
        var name    = eq > 0 ? body[..eq].Trim() : string.Empty;
        var value   = eq > 0 ? body[(eq + 1)..].Trim() : string.Empty;
        return name.Length == 0 || value.Length == 0 ? null : (enabled, name, value);
    }

    private static Dictionary<string, string> ToMap(List<Element> elements)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in elements) map.TryAdd(e.Key, e.Text);
        return map;
    }

    private static bool Split(CollectionShape shape, string text, out List<Element> elements, out bool wrapped)
    {
        elements = [];
        wrapped  = false;
        if (string.IsNullOrWhiteSpace(text)) return true;

        if (shape == CollectionShape.Lines)
        {
            foreach (var line in text.Split('\n'))
            {
                var kept = line.TrimEnd('\r');
                if (kept.Trim().Length > 0) elements.Add(new Element(kept.Trim(), kept));
            }
            return true;
        }

        if (shape == CollectionShape.NameValueLines)
        {
            // An entry is its name and whether it is on, then its rank among those (a disabled entry and an active one may
            // share a name, and one added elsewhere shifts neither); a line that is no entry is keyed by its text.
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in text.Split('\n'))
            {
                var kept = line.TrimEnd('\r');
                var trimmed = kept.Trim();
                if (trimmed.Length == 0) continue;
                string key;
                if (NameValue(trimmed) is { } entry)
                {
                    var id = (entry.Enabled ? "on\n" : "off\n") + entry.Name.ToLowerInvariant();
                    key = id + "\n" + (seen[id] = seen.GetValueOrDefault(id) + 1);
                }
                else key = "\n" + trimmed;
                elements.Add(new Element(key, kept));
            }
            return true;
        }

        try
        {
            var root = JsonNode.Parse(text);
            if (shape == CollectionShape.JsonObjectByName)
            {
                if (root is not JsonObject obj) return false;
                if (obj.Count == 1 && obj[McpWrapper] is JsonObject inner) { obj = inner; wrapped = true; }
                foreach (var (name, value) in obj)
                    elements.Add(new Element(name, value?.ToJsonString() ?? "null"));
                return true;
            }

            if (root is not JsonArray array) return false;
            foreach (var item in array)
            {
                if (item is not JsonObject site || site["id"]?.GetValueKind() != JsonValueKind.String) return false;
                elements.Add(new Element(site["id"]!.GetValue<string>(), site.ToJsonString()));
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private const string McpWrapper = "mcpServers";

    private static string Join(CollectionShape shape, List<Element> elements, string mine, bool wrapped)
    {
        switch (shape)
        {
            case CollectionShape.Lines:
            case CollectionShape.NameValueLines:
            {
                var eol = mine.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var joined = string.Join(eol, elements.Select(e => e.Text));
                return mine.EndsWith('\n') && joined.Length > 0 ? joined + eol : joined;
            }
            case CollectionShape.JsonObjectByName:
            {
                var obj = new JsonObject();
                foreach (var e in elements) obj[e.Key] = JsonNode.Parse(e.Text);
                var root = wrapped ? new JsonObject { [McpWrapper] = obj } : obj;
                return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
            default:
            {
                var array = new JsonArray();
                foreach (var e in elements) array.Add(JsonNode.Parse(e.Text));
                return array.ToJsonString();
            }
        }
    }
}
