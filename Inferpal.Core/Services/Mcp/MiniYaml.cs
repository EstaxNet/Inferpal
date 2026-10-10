using System.Text.Json.Nodes;

namespace Inferpal.Services.Mcp;

/// <summary>
/// The YAML a Continue block file (<c>.continue/mcpServers/*.yaml</c>) is written in, read into JSON nodes: block maps
/// and block lists by indentation, <c>- key: value</c> items, plain, single- and double-quoted scalars, inline
/// <c>[a, b]</c> and <c>{}</c>, comments. Anything else — anchors, multi-document, block scalars — is refused by
/// <see cref="FormatException"/>, never guessed at.
/// </summary>
/// <remarks>
/// ⚠ A reader for these files only: no YAML library ships with the product. What it does not read, it says (the server
/// file is named with the reason), so a file outside the subset is a visible refusal, not a server with wrong values.
/// </remarks>
internal static class MiniYaml
{
    private sealed record Line(int Indent, string Text, int Number);

    public static JsonNode? Parse(string text)
    {
        var lines = new List<Line>();
        var n = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            n++;
            var content = StripComment(raw).TrimEnd();
            if (content.Trim().Length == 0) continue;
            if (content.TrimStart().StartsWith("---", StringComparison.Ordinal) && lines.Count > 0)
                throw new FormatException($"line {n}: several YAML documents in one file");
            if (content.Trim() == "---") continue;
            if (content.Contains('\t')) throw new FormatException($"line {n}: a tab in the indentation");
            var indent = content.Length - content.TrimStart().Length;
            lines.Add(new Line(indent, content.Trim(), n));
        }
        var at = 0;
        return lines.Count == 0 ? null : Block(lines, ref at, lines[0].Indent);
    }

    private static JsonNode Block(List<Line> lines, ref int at, int indent) =>
        lines[at].Text.StartsWith("- ", StringComparison.Ordinal) || lines[at].Text == "-"
            ? List(lines, ref at, indent)
            : Map(lines, ref at, indent);

    private static JsonArray List(List<Line> lines, ref int at, int indent)
    {
        var list = new JsonArray();
        while (at < lines.Count && lines[at].Indent == indent && (lines[at].Text.StartsWith("- ", StringComparison.Ordinal) || lines[at].Text == "-"))
        {
            var line = lines[at];
            var rest = line.Text.Length > 1 ? line.Text[2..].TrimStart() : "";
            if (rest.Length == 0)
            {
                at++;
                list.Add(at < lines.Count && lines[at].Indent > indent ? Block(lines, ref at, lines[at].Indent) : null);
                continue;
            }
            if (KeyOf(rest) is not null)
            {
                // "- key: value" opens a map whose other keys sit under the first one's column.
                var column = indent + (line.Text.Length - rest.Length);
                lines[at] = line with { Indent = column, Text = rest };
                list.Add(Map(lines, ref at, column));
                continue;
            }
            list.Add(Scalar(rest, line.Number));
            at++;
        }
        return list;
    }

    private static JsonObject Map(List<Line> lines, ref int at, int indent)
    {
        var map = new JsonObject();
        while (at < lines.Count && lines[at].Indent == indent)
        {
            var line = lines[at];
            if (KeyOf(line.Text) is not { } key) throw new FormatException($"line {line.Number}: expected \"key: value\"");
            var value = line.Text[(KeyEnd(line.Text) + 1)..].Trim();
            at++;
            if (value is "|" or ">" or "|-" or ">-" or "|+" or ">+")
                throw new FormatException($"line {line.Number}: a block scalar");
            if (value.StartsWith('&') || value.StartsWith('*'))
                throw new FormatException($"line {line.Number}: an anchor or alias");
            if (value.Length > 0) { map[key] = Scalar(value, line.Number); continue; }
            map[key] = at < lines.Count && lines[at].Indent > indent ? Block(lines, ref at, lines[at].Indent)
                     : at < lines.Count && lines[at].Indent == indent && lines[at].Text.StartsWith("- ", StringComparison.Ordinal)
                         ? List(lines, ref at, indent)   // a list at the key's own column, legal in YAML
                         : null;
        }
        if (at < lines.Count && lines[at].Indent > indent)
            throw new FormatException($"line {lines[at].Number}: unexpected indentation");
        return map;
    }

    /// <summary>The key of a <c>key: value</c> line (quoted or not); <c>null</c> when the line is none.</summary>
    private static string? KeyOf(string text)
    {
        var end = KeyEnd(text);
        if (end <= 0 || end >= text.Length || text[end] != ':') return null;
        if (end + 1 < text.Length && text[end + 1] != ' ') return null;     // "http://x" is no key
        var key = text[..end].Trim();
        return key.Length >= 2 && (key[0] == '"' || key[0] == '\'') && key[^1] == key[0] ? key[1..^1] : key;
    }

    private static int KeyEnd(string text)
    {
        if (text.Length > 0 && (text[0] == '"' || text[0] == '\''))
        {
            var close = text.IndexOf(text[0], 1);
            return close < 0 ? -1 : close + 1;
        }
        var i = 0;
        while (i < text.Length && text[i] != ':') i++;
        return i;
    }

    /// <summary>
    /// The escapes of a double-quoted YAML scalar (YAML 1.2, §5.7) — and only those.
    /// </summary>
    /// <remarks>
    /// ⚠ Not <c>Regex.Unescape</c>: its set is the regex syntax's, not YAML's, and on an escape it does not know it throws
    /// an <see cref="ArgumentException"/> no reader of a file expects — a Windows path between double quotes
    /// (<c>"C:\qux"</c>) stopped every MCP server, the user's own included. An escape YAML does not know is a
    /// <see cref="FormatException"/> naming its line, which the file's reader reports.
    /// </remarks>
    private static string Unescape(string text, int number)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { sb.Append(text[i]); continue; }
            if (++i == text.Length) throw new FormatException($"line {number}: a lone backslash ends the value");
            var c = text[i];
            switch (c)
            {
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 't': case '\t': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'v': sb.Append('\v'); break;
                case 'f': sb.Append('\f'); break;
                case 'r': sb.Append('\r'); break;
                case 'e': sb.Append('\u001b'); break;
                case ' ': case '"': case '/': case '\\': sb.Append(c); break;
                case 'N': sb.Append('\u0085'); break;
                case '_': sb.Append('\u00a0'); break;
                case 'L': sb.Append('\u2028'); break;
                case 'P': sb.Append('\u2029'); break;
                case 'x': case 'u': case 'U':
                {
                    var digits = c == 'x' ? 2 : c == 'u' ? 4 : 8;
                    if (text.Length - (i + 1) < digits
                        || !int.TryParse(text.AsSpan(i + 1, digits), System.Globalization.NumberStyles.AllowHexSpecifier,
                                         System.Globalization.CultureInfo.InvariantCulture, out var code)
                        || code is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
                        throw new FormatException($"line {number}: \\{c} needs {digits} hexadecimal digits of a character");
                    sb.Append(char.ConvertFromUtf32(code));
                    i += digits;
                    break;
                }
                default:
                    throw new FormatException($"line {number}: \\{c} is not a YAML escape (single quotes keep a backslash as it is)");
            }
        }
        return sb.ToString();
    }

    private static JsonNode? Scalar(string value, int number)
    {
        if (value.StartsWith('"'))
        {
            if (!value.EndsWith('"') || value.Length < 2) throw new FormatException($"line {number}: an unclosed quote");
            return JsonValue.Create(Unescape(value[1..^1], number));
        }
        if (value.StartsWith('\''))
        {
            if (!value.EndsWith('\'') || value.Length < 2) throw new FormatException($"line {number}: an unclosed quote");
            return JsonValue.Create(value[1..^1].Replace("''", "'"));
        }
        if (value == "{}") return new JsonObject();
        if (value.StartsWith('['))
        {
            if (!value.EndsWith(']')) throw new FormatException($"line {number}: an unclosed list");
            var array = new JsonArray();
            foreach (var item in SplitInline(value[1..^1]))
                array.Add(Scalar(item, number));
            return array;
        }
        if (value.StartsWith('{')) throw new FormatException($"line {number}: an inline map");
        if (value is "null" or "~") return null;
        if (value is "true" or "false") return JsonValue.Create(value == "true");
        return JsonValue.Create(value);
    }

    private static IEnumerable<string> SplitInline(string inner)
    {
        var start = 0;
        char? quote = null;
        for (var i = 0; i < inner.Length; i++)
        {
            if (quote is null && inner[i] is '"' or '\'') quote = inner[i];
            else if (quote == inner[i]) quote = null;
            else if (quote is null && inner[i] == ',')
            {
                if (inner[start..i].Trim() is { Length: > 0 } item) yield return item;
                start = i + 1;
            }
        }
        if (inner[start..].Trim() is { Length: > 0 } last) yield return last;
    }

    /// <summary>The line without its comment: a <c>#</c> at its start or after a blank, outside quotes.</summary>
    private static string StripComment(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            if (quote is null && line[i] is '"' or '\'') quote = line[i];
            else if (quote == line[i]) quote = null;
            else if (quote is null && line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line[..i];
        }
        return line;
    }
}
