using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inferpal.Services.Documents;

/// <summary>
/// One cell of a Jupyter notebook changed, inserted or deleted — the rest of the file written back as Jupyter writes it, so an
/// untouched cell, its outputs and the notebook's settings are the same bytes.
/// </summary>
/// <remarks>
/// ⚠ Rewritten by a JSON serializer, a notebook Jupyter wrote (one-space indent, raw non-ASCII, its own key order) changes on
/// every line: the diff is the whole file and git blames every cell. <see cref="Write"/> writes what <c>nbformat</c> writes
/// (<c>json.dumps(nb, indent=1, ensure_ascii=False)</c> plus a final line break), keys in the order the file holds them.
/// </remarks>
internal static class NotebookEdit
{
    /// <summary>A change made: the new file's text, and the cell's source before and after (what the card shows).</summary>
    /// <param name="Number">The cell's number from 1, as the notebook reads it after the change (the deleted one: before).</param>
    /// <param name="Cells">How many cells the notebook holds after the change.</param>
    internal sealed record Change(string Text, string OldSource, string NewSource, int Number, string CellType, int Cells);

    internal static readonly string[] Actions = ["replace", "insert_after", "insert_before", "delete"];

    /// <summary>
    /// Applies <paramref name="action"/> to the cell named by <paramref name="cell"/> (its id, or its number from 1); the
    /// refusal, said for the model, when the notebook or the cell is not one it can change.
    /// </summary>
    internal static (Change? Change, string? Refusal) Apply(string text, string cell, string action, string? cellType, string? source)
    {
        JsonObject root;
        try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject ?? throw new JsonException(); }
        // A duplicate key or a lone surrogate throws other than JsonException; NaN and Infinity (which Python writes) are
        // not JSON either: refused, never half-read.
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        { return (null, "the file is not a notebook's JSON this tool can rewrite (malformed, or a value such as NaN or Infinity): it cannot be changed cell by cell"); }
        if (root["cells"] is not JsonArray cells) return (null, "the file holds no list of cells");
        if (!Actions.Contains(action)) return (null, $"unknown action '{action}' (expected {string.Join(", ", Actions)})");
        if (cellType is not null and not ("code" or "markdown" or "raw")) return (null, $"unknown cell_type '{cellType}' (expected code, markdown or raw)");

        // An empty notebook gets its first cell from either insert: there is no cell to name.
        if (cells.Count == 0 && action is "insert_after" or "insert_before")
        {
            if (source is null) return (null, "'source' is required to insert a cell");
            cells.Add(NewCell(cellType ?? "code", source, null, null));
            return (new Change(Write(root, text), string.Empty, source, 1, cellType ?? "code", cells.Count), null);
        }
        var index = Find(cells, cell);
        if (index < 0) return (null, cells.Count == 0 ? "the notebook has no cells: insert one (insert_after)" : $"no cell '{cell}' in this notebook. Its cells: {Listing(cells)}");
        var target = (JsonObject)cells[index]!;
        var oldSource = Source(target);
        var oldType = target["cell_type"]?.GetValue<string>() ?? "code";

        switch (action)
        {
            case "delete":
                cells.RemoveAt(index);
                return (new Change(Write(root, text), oldSource, string.Empty, index + 1, oldType, cells.Count), null);
            case "replace":
                if (source is null) return (null, "'source' is required to replace a cell");
                var type = cellType ?? oldType;
                if (type != oldType)
                {
                    cells[index] = NewCell(type, source, target["id"]?.GetValue<string>(), (target["metadata"] as JsonObject)?.DeepClone());
                    return (new Change(Write(root, text), oldSource, source, index + 1, type, cells.Count), null);
                }
                target["source"] = Lines(source);
                // The outputs said what the OLD code printed: kept, they would read as this code's.
                if (type == "code")
                {
                    target["outputs"] = new JsonArray();
                    target["execution_count"] = null;
                }
                return (new Change(Write(root, text), oldSource, source, index + 1, type, cells.Count), null);
            default:
                if (source is null) return (null, "'source' is required to insert a cell");
                var at = action == "insert_after" ? index + 1 : index;
                var hasIds = cells.OfType<JsonObject>().Any(c => c.ContainsKey("id"));
                cells.Insert(at, NewCell(cellType ?? "code", source, hasIds ? NewId(cells) : null, null));
                return (new Change(Write(root, text), string.Empty, source, at + 1, cellType ?? "code", cells.Count), null);
        }
    }

    /// <summary>The cell named by its id (exact), else by its number from 1 (<c>#3</c>, <c>03</c>); -1 for none. One rule for
    /// the tool that edits a cell and the one that shows a cell's output.</summary>
    internal static int Find(IReadOnlyList<string?> ids, string cell)
    {
        for (var i = 0; i < ids.Count; i++)
            if (ids[i] == cell) return i;
        return int.TryParse(cell.Trim().TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= ids.Count
            ? n - 1 : -1;
    }

    private static int Find(JsonArray cells, string cell) =>
        Find([.. cells.Select(c => c is JsonObject o && o["id"] is JsonValue id && id.TryGetValue<string>(out var s) ? s : null)], cell);

    private static string Listing(JsonArray cells) =>
        string.Join("; ", cells.Select((c, i) =>
        {
            var o = c as JsonObject;
            var first = Source(o).Split('\n')[0];
            return $"{i + 1}{(o?["id"] is JsonValue id ? $" (id {id})" : "")} {o?["cell_type"]}: {(first.Length > 40 ? first[..40] + "…" : first)}";
        }).Take(40)) + (cells.Count > 40 ? $"; and {cells.Count - 40} more" : "");

    /// <summary>A cell's source as one text: nbformat holds it as a list of lines, or as one string.</summary>
    internal static string Source(JsonObject? cell) => cell?["source"] switch
    {
        JsonArray lines => string.Concat(lines.Select(l => l?.GetValue<string>() ?? string.Empty)),
        JsonValue one when one.TryGetValue<string>(out var s) => s,
        _ => string.Empty,
    };

    /// <summary>A source as nbformat writes it: Python's <c>splitlines(keepends=True)</c> — each line keeping its break, a
    /// CRLF one break, and the other breaks Python knows (\r, \v, \f, \x1c-\x1e, \x85, U+2028, U+2029) too.</summary>
    private static JsonArray Lines(string source)
    {
        var lines = new JsonArray();
        var start = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c is not ('\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')) continue;
            if (c == '\r' && i + 1 < source.Length && source[i + 1] == '\n') i++;
            lines.Add(source[start..(i + 1)]);
            start = i + 1;
        }
        if (start < source.Length) lines.Add(source[start..]);
        return lines;
    }

    /// <summary>Whether <paramref name="text"/> is already laid out as Jupyter writes it: when not (another indent, escaped
    /// non-ASCII), any change rewrites every line, and the card says so.</summary>
    internal static bool InJupytersLayout(string text)
    {
        try { return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) is { } root && Write(root, text) == text; }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }

    private static JsonObject NewCell(string type, string source, string? id, JsonNode? metadata)
    {
        // Keys in nbformat's order (it sorts them): cell_type, execution_count, id, metadata, outputs, source.
        var cell = new JsonObject { ["cell_type"] = type };
        if (type == "code") cell["execution_count"] = null;
        if (id is not null) cell["id"] = id;
        cell["metadata"] = metadata ?? new JsonObject();
        if (type == "code") cell["outputs"] = new JsonArray();
        cell["source"] = Lines(source);
        return cell;
    }

    /// <summary>An id no cell of the notebook has, in nbformat's shape (8 hex characters).</summary>
    private static string NewId(JsonArray cells)
    {
        var taken = cells.OfType<JsonObject>().Select(c => c["id"]?.ToString()).ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (!taken.Contains(id)) return id;
        }
    }

    /// <summary>The notebook as nbformat writes it, in the line breaks and final break of <paramref name="original"/>.</summary>
    internal static string Write(JsonNode root, string original)
    {
        var sb = new StringBuilder();
        WriteNode(sb, root, 0);
        var text = sb.ToString();
        if (original.EndsWith('\n')) text += "\n";
        return original.Contains("\r\n", StringComparison.Ordinal) ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;
    }

    private static void WriteNode(StringBuilder sb, JsonNode? node, int depth)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonObject o when o.Count == 0: sb.Append("{}"); break;
            case JsonObject o:
                sb.Append("{\n");
                var first = true;
                foreach (var (key, value) in o)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append(' ', depth + 1);
                    Str(sb, key);
                    sb.Append(": ");
                    WriteNode(sb, value, depth + 1);
                }
                sb.Append('\n').Append(' ', depth).Append('}');
                break;
            case JsonArray a when a.Count == 0: sb.Append("[]"); break;
            case JsonArray a:
                sb.Append("[\n");
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(",\n");
                    sb.Append(' ', depth + 1);
                    WriteNode(sb, a[i], depth + 1);
                }
                sb.Append('\n').Append(' ', depth).Append(']');
                break;
            case JsonValue v when v.TryGetValue<string>(out var s): Str(sb, s); break;
            // Numbers, booleans: their own spelling, as the file wrote them.
            default: sb.Append(node.ToJsonString()); break;
        }
    }

    /// <summary>A string as Python's <c>json.dumps(…, ensure_ascii=False)</c> writes it.</summary>
    private static void Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        sb.Append('"');
    }
}
