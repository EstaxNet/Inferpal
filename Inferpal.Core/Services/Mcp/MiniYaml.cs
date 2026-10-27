using System.Text.Json.Nodes;

namespace Inferpal.Services.Mcp;

/// <summary>
/// The YAML of Continue's files (<c>config.yaml</c>, <c>.continue/mcpServers/*.yaml</c>) and Roo's modes, read into JSON
/// nodes: block maps and block lists by indentation, <c>- key: value</c> items, plain, single- and double-quoted scalars,
/// literal and folded block scalars (<c>|</c>, <c>&gt;</c>, with their chomping), anchors, aliases and merge keys
/// (<c>&amp;x</c>, <c>*x</c>, <c>&lt;&lt;: *x</c>), inline <c>[a, b]</c> and <c>{a: b}</c>, comments, a leading directive
/// and <c>---</c>. Anything else — several documents, tabs in the indentation, an alias to no anchor — is refused by
/// <see cref="FormatException"/> naming its line, never guessed at.
/// </summary>
/// <remarks>
/// <para>⚠ A reader for these files only: no YAML library ships with the product. What it does not read, it says (the
/// file is named with the reason), so a file outside the subset is a visible refusal, not wrong values.</para>
/// <para>⚠ A block scalar's lines are taken RAW, before comments and blank lines are dropped: in a prompt or a rule a
/// <c>#</c> is a Markdown heading, a blank line separates paragraphs, and a tab is code. Numbers stay strings — the
/// reader of a key decides what it is.</para>
/// </remarks>
internal static class MiniYaml
{
    public static JsonNode? Parse(string text) => new Parser().Parse(text);

    /// <param name="Block">The value of a block scalar opened on this line (its indicator removed from <see cref="Text"/>).</param>
    private sealed record Line(int Indent, string Text, int Number, string? Block = null);

    private sealed class Parser
    {
        private readonly Dictionary<string, JsonNode?> _anchors = new(StringComparer.Ordinal);

        /// <summary>How deep lists and maps may nest, in blocks or inline. ⚠ Every level is a call: a file of one
        /// line of 20,000 dashes or brackets overflowed the stack, and nothing catches that — the host died.</summary>
        private const int MaxDepth = 64;
        private int _depth;

        private Depth Deeper(int number) =>
            ++_depth > MaxDepth ? throw new FormatException($"line {number}: nested more than {MaxDepth} levels deep") : new Depth(this);

        private readonly struct Depth(Parser parser) : IDisposable
        {
            public void Dispose() => parser._depth--;
        }

        public JsonNode? Parse(string text)
        {
            var raw = text.Replace("\r\n", "\n").Split('\n');
            var lines = new List<Line>();
            for (var i = 0; i < raw.Length; i++)
            {
                var n = i + 1;
                var content = StripComment(raw[i]).TrimEnd();
                var trimmed = content.Trim();
                if (trimmed.Length == 0) continue;
                if (lines.Count == 0 && trimmed.StartsWith('%')) continue;           // %YAML 1.1, before the document
                if (trimmed.StartsWith("---", StringComparison.Ordinal) && lines.Count > 0)
                    throw new FormatException($"line {n}: several YAML documents in one file");
                if (trimmed == "---") continue;
                var indent = content.Length - content.TrimStart().Length;
                if (content[..indent].Contains('\t')) throw new FormatException($"line {n}: a tab in the indentation");
                var line = new Line(indent, trimmed, n);
                if (BlockIndicator(trimmed) is { } indicator)
                {
                    var (body, next) = BlockBody(raw, i + 1, ParentColumn(indent, trimmed), indicator, n);
                    line = line with { Text = trimmed[..^indicator.Length].TrimEnd(), Block = body };
                    i = next - 1;
                }
                lines.Add(line);
            }
            var at = 0;
            return lines.Count == 0 ? null : Block(lines, ref at, lines[0].Indent);
        }

        // ── Structure ───────────────────────────────────────────────────────────

        private JsonNode? Block(List<Line> lines, ref int at, int indent) =>
            IsItem(lines[at].Text) ? List(lines, ref at, indent) : Map(lines, ref at, indent);

        private static bool IsItem(string text) => text.StartsWith("- ", StringComparison.Ordinal) || text == "-";

        private JsonArray List(List<Line> lines, ref int at, int indent)
        {
            using var _ = Deeper(lines[at].Number);
            var list = new JsonArray();
            while (at < lines.Count && lines[at].Indent == indent && IsItem(lines[at].Text))
            {
                var line = lines[at];
                var rest = line.Text.Length > 1 ? line.Text[2..].TrimStart() : "";
                var anchor = TakeAnchor(ref rest);
                if (rest.Length == 0)
                {
                    at++;
                    var node = line.Block is not null ? JsonValue.Create(line.Block)
                             : at < lines.Count && lines[at].Indent > indent ? Block(lines, ref at, lines[at].Indent)
                             : null;
                    list.Add(Anchor(anchor, node));
                    continue;
                }
                if (IsItem(rest))
                {
                    // "- - item" opens a list inside the list, whose other items sit under the second dash — Roo's
                    // "- - edit" then "- fileRegex: …".
                    var column = indent + (line.Text.Length - rest.Length);
                    lines[at] = line with { Indent = column, Text = rest };
                    list.Add(Anchor(anchor, List(lines, ref at, column)));
                    continue;
                }
                if (KeyOf(rest) is not null)
                {
                    // "- key: value" opens a map whose other keys sit under the first one's column.
                    var column = indent + (line.Text.Length - rest.Length);
                    lines[at] = line with { Indent = column, Text = rest };
                    list.Add(Anchor(anchor, Map(lines, ref at, column)));
                    continue;
                }
                list.Add(Anchor(anchor, Scalar(rest, line.Number)));
                at++;
            }
            return list;
        }

        private JsonObject Map(List<Line> lines, ref int at, int indent)
        {
            using var _ = Deeper(lines[at].Number);
            var map = new JsonObject();
            var merges = new List<JsonObject>();
            while (at < lines.Count && lines[at].Indent == indent)
            {
                var line = lines[at];
                if (KeyOf(line.Text) is not { } key) throw new FormatException($"line {line.Number}: expected \"key: value\"");
                var value = line.Text[(KeyEnd(line.Text) + 1)..].Trim();
                at++;
                var anchor = TakeAnchor(ref value);
                JsonNode? node;
                if (line.Block is not null) node = JsonValue.Create(line.Block);
                else if (value.Length > 0) node = Scalar(value, line.Number);
                else node = at < lines.Count && lines[at].Indent > indent ? Block(lines, ref at, lines[at].Indent)
                          : at < lines.Count && lines[at].Indent == indent && IsItem(lines[at].Text)
                              ? List(lines, ref at, indent)   // a list at the key's own column, legal in YAML
                              : null;
                node = Anchor(anchor, node);
                if (key == "<<")
                {
                    // A merge key: the maps it names give their keys to this one — the keys written here win, wherever
                    // they stand.
                    IEnumerable<JsonNode?> sources = node is JsonArray many ? many : new[] { node };
                    foreach (var source in sources)
                        merges.Add(source as JsonObject
                                   ?? throw new FormatException($"line {line.Number}: \"<<\" merges a map, not a value"));
                    continue;
                }
                map[key] = node;
            }
            if (at < lines.Count && lines[at].Indent > indent)
                throw new FormatException($"line {lines[at].Number}: unexpected indentation");
            foreach (var source in merges)
                foreach (var (key, value) in source)
                    if (!map.ContainsKey(key)) map[key] = value?.DeepClone();
            return map;
        }

        // ── Anchors and aliases ─────────────────────────────────────────────────

        /// <summary>Takes a leading <c>&amp;name</c> off <paramref name="value"/>; the name, or <c>null</c>.</summary>
        private static string? TakeAnchor(ref string value)
        {
            if (!value.StartsWith('&')) return null;
            var end = value.IndexOf(' ');
            var name = end < 0 ? value[1..] : value[1..end];
            value = end < 0 ? string.Empty : value[(end + 1)..].TrimStart();
            return name;
        }

        private JsonNode? Anchor(string? name, JsonNode? node)
        {
            if (name is not null) _anchors[name] = node;
            return node;
        }

        private JsonNode? Alias(string value, int number)
        {
            var name = value[1..].Trim();
            if (!_anchors.TryGetValue(name, out var node))
                throw new FormatException($"line {number}: the alias *{name} names no anchor written before it");
            return node?.DeepClone();
        }

        // ── Block scalars ───────────────────────────────────────────────────────

        /// <summary>The indicator ending a <c>key: |</c> or <c>- &gt;-</c> line; <c>null</c> when the value is none.</summary>
        private static string? BlockIndicator(string text)
        {
            var value = text;
            if (IsItem(value)) value = value.Length > 1 ? value[2..].TrimStart() : "";
            if (KeyOf(value) is not null) value = value[(KeyEnd(value) + 1)..].Trim();
            TakeAnchor(ref value);
            return value.Length is >= 1 and <= 3 && value[0] is '|' or '>'
                   && value[1..].All(c => c is '+' or '-' || char.IsAsciiDigit(c))
                ? value
                : null;
        }

        /// <summary>The column a block scalar's lines must be deeper than: the key's, or the list item's.</summary>
        private static int ParentColumn(int indent, string text)
        {
            if (!IsItem(text)) return indent;
            var rest = text.Length > 1 ? text[2..].TrimStart() : "";
            return KeyOf(rest) is not null ? indent + (text.Length - rest.Length) : indent;
        }

        /// <summary>The value of a block scalar whose lines start at <paramref name="from"/>, and the line after it.</summary>
        private static (string Body, int Next) BlockBody(string[] raw, int from, int parent, string indicator, int number)
        {
            var explicitIndent = indicator.FirstOrDefault(char.IsAsciiDigit);
            var column = explicitIndent == default ? -1 : parent + (explicitIndent - '0');
            var body = new List<string>();
            var i = from;
            for (; i < raw.Length; i++)
            {
                var line = raw[i].TrimEnd('\r');
                if (line.Trim().Length == 0) { body.Add(string.Empty); continue; }
                var indent = line.Length - line.TrimStart(' ').Length;
                if (column < 0)
                {
                    if (indent <= parent) break;
                    column = indent;
                }
                if (indent < column) break;
                body.Add(line[column..]);
            }
            // Blank lines past the last line of text belong to the document, unless "+" keeps them.
            var lastText = body.FindLastIndex(l => l.Length > 0);
            var trailing = body.Count - lastText - 1;
            var next = i - (indicator.Contains('+') ? 0 : trailing);
            var lines = body.Take(lastText + 1).ToList();

            string text;
            if (indicator[0] == '|') text = string.Join("\n", lines);
            else
            {
                // Folded: lines of one paragraph join with a space; a blank line is a line break; a more indented line
                // keeps its own.
                var sb = new System.Text.StringBuilder();
                bool previousText = false, previousMore = false;
                foreach (var l in lines)
                {
                    if (l.Length == 0) { sb.Append('\n'); previousText = false; continue; }
                    var more = l[0] == ' ' || l[0] == '\t';
                    if (previousText) sb.Append(more || previousMore ? '\n' : ' ');
                    sb.Append(l);
                    previousText = true;
                    previousMore = more;
                }
                text = sb.ToString();
            }
            if (lastText < 0) return (string.Empty, next);
            return indicator.Contains('-') ? (text, next)
                 : indicator.Contains('+') ? (text + new string('\n', trailing + 1), next)
                 : (text + "\n", next);
        }

        // ── Scalars ─────────────────────────────────────────────────────────────

        private JsonNode? Scalar(string value, int number)
        {
            if (value.StartsWith('*')) return Alias(value, number);
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
            if (value.StartsWith('['))
            {
                if (!value.EndsWith(']')) throw new FormatException($"line {number}: an unclosed list");
                using var _ = Deeper(number);
                var array = new JsonArray();
                foreach (var item in SplitInline(value[1..^1]))
                    array.Add(Scalar(item, number));
                return array;
            }
            if (value.StartsWith('{'))
            {
                if (!value.EndsWith('}')) throw new FormatException($"line {number}: an unclosed map");
                using var _ = Deeper(number);
                var map = new JsonObject();
                foreach (var item in SplitInline(value[1..^1]))
                {
                    if (KeyOf(item) is not { } key) throw new FormatException($"line {number}: expected \"key: value\" in {{…}}");
                    map[key] = Scalar(item[(KeyEnd(item) + 1)..].Trim(), number);
                }
                return map;
            }
            if (value is "null" or "~") return null;
            if (value is "true" or "false") return JsonValue.Create(value == "true");
            return JsonValue.Create(value);
        }
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
