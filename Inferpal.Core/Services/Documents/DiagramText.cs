using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Inferpal.Services.Documents;

/// <summary>
/// A diagram read as text, page by page: its shapes (their text) and its links (<c>A → B: label</c>) — draw.io
/// (<c>.drawio</c>, plain or compressed) and Visio (<c>.vsdx</c>). For a model that does not see, or a diagram it would see
/// too small to read.
/// </summary>
/// <remarks>Positions, colours and styles are left out, and said. A shape with no text is counted, and named in a link by
/// its id; a draw.io label written as HTML is read as its text.</remarks>
internal static class DiagramText
{
    /// <summary>The largest part read in a diagram: past it, the diagram is said too large.</summary>
    private const long MaxPartBytes = 16L * 1024 * 1024;

    private sealed record Page(string Name, List<string> Shapes, List<string> Links, int Unnamed);

    /// <summary>
    /// A draw.io file opened by the agent: its shapes and links in place of its first page, within <paramref name="budget"/>
    /// characters; <c>null</c> for any other file, or one that does not read as a diagram (its XML is shown instead).
    /// </summary>
    /// <remarks>⚠ The file stays a text file: <c>start_line</c> reads its XML, so the agent can edit it — read only as
    /// shapes, it could neither quote the XML nor rewrite the file.</remarks>
    internal static string? ForRead(string name, string xml, int budget, CancellationToken ct)
    {
        if (!Path.GetExtension(name).Equals(".drawio", StringComparison.OrdinalIgnoreCase)) return null;
        // Through the documents' reader: its deadline, the person's Stop, and its handling of a damaged file.
        var read = DocumentText.Extract(Encoding.UTF8.GetBytes(xml), ".drawio", ct);
        if (read.Problem == DocumentProblem.TooSlow)
            return $"[{name}: reading it as a diagram took longer than {DocumentText.Deadline.TotalSeconds:0} s. Read its XML with start_line=1.]";
        if (read.Problem != DocumentProblem.None) return null;
        var head = read.Header + "\n[This is the diagram read as text; read its XML with start_line=1.]\n\n";
        var room = Math.Max(0, budget - head.Length - 160);
        return read.Text.Length <= room ? head + read.Text
             : head + read.Text[..room] + string.Create(CultureInfo.InvariantCulture,
                 $"\n… {read.Text.Length - room} more characters of the diagram's text are not shown here: ask the user to attach the file, which reads it whole.\n");
    }

    /// <summary>The text of the diagram in <paramref name="bytes"/>, by its <paramref name="kind"/> (<c>.drawio</c> or
    /// <c>.vsdx</c>).</summary>
    internal static DocumentExtraction Extract(byte[] bytes, string kind, CancellationToken ct)
    {
        var pages = kind == ".vsdx" ? Visio(bytes, ct) : DrawIo(bytes, ct);
        if (pages.Count == 0 || pages.All(p => p.Shapes.Count == 0 && p.Links.Count == 0))
            return new(string.Empty, 0, 0, DocumentProblem.NoText);

        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            text.Append("## ").Append(page.Name).Append('\n');
            if (page.Shapes.Count > 0)
            {
                text.Append("Shapes:\n");
                foreach (var shape in page.Shapes) text.Append("- ").Append(shape).Append('\n');
            }
            if (page.Unnamed > 0) text.Append(inv, $"({page.Unnamed} shape(s) with no text are not listed.)\n");
            if (page.Links.Count > 0)
            {
                text.Append("Links:\n");
                foreach (var link in page.Links) text.Append("- ").Append(link).Append('\n');
            }
            text.Append('\n');
        }
        var cut = text.Length > PdfText.MaxChars;
        var cutNote = cut ? string.Create(inv, $" Reading stopped after {PdfText.MaxChars:N0} characters: the rest is not here.") : string.Empty;
        var header = string.Create(inv,
            $"[Diagram ({(kind == ".vsdx" ? "Visio" : "draw.io")}), {pages.Count} page(s): its shapes and links as text; positions, colours and styles are not here.{cutNote}]");
        return new(cut ? text.ToString(0, PdfText.MaxChars) : text.ToString(), pages.Count, 0, DocumentProblem.None, cut ? 1 : 0, header);
    }

    // ── draw.io ─────────────────────────────────────────────────────────────────────

    private static List<Page> DrawIo(byte[] bytes, CancellationToken ct)
    {
        var root = Load(new MemoryStream(bytes));
        var pages = new List<Page>();
        if (root.Name.LocalName == "mxGraphModel") { pages.Add(DrawIoPage("Page 1", root)); return pages; }
        var number = 0;
        var inflated = 0L;   // bounds the pages together: a page alone is bounded by MaxPartBytes
        foreach (var diagram in root.Elements().Where(e => e.Name.LocalName == "diagram"))
        {
            ct.ThrowIfCancellationRequested();
            number++;
            var name = (string?)diagram.Attribute("name") ?? string.Create(CultureInfo.InvariantCulture, $"Page {number}");
            var model = diagram.Elements().FirstOrDefault(e => e.Name.LocalName == "mxGraphModel")
                        ?? Inflated(diagram.Value.Trim(), ref inflated);
            if (model is not null) pages.Add(DrawIoPage(name, model));
        }
        return pages;
    }

    /// <summary>A page draw.io saved compressed: base64, then raw deflate, then URL encoding, of its model.</summary>
    private static XElement? Inflated(string packed, ref long inflated)
    {
        if (packed.Length == 0) return null;
        byte[] deflated;
        try { deflated = Convert.FromBase64String(packed); }
        catch (FormatException) { throw new InvalidDataException("a draw.io page that is neither XML nor compressed"); }
        using var inflate = new DeflateStream(new MemoryStream(deflated), CompressionMode.Decompress);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = inflate.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxPartBytes || inflated + buffer.Length > MaxPartBytes * 4)
                throw new DocumentLimitException("A draw.io diagram past its size limit");
        }
        inflated += buffer.Length;
        var xml = Uri.UnescapeDataString(Encoding.UTF8.GetString(buffer.ToArray()));
        return Load(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
    }

    private static Page DrawIoPage(string name, XElement model)
    {
        // A cell's label and id may sit on the object that wraps it (<UserObject label=…>, <object label=…>).
        var cells = model.Descendants().Where(e => e.Name.LocalName == "mxCell").Select(c =>
        {
            var wrapper = c.Parent is { } p && p.Name.LocalName is "UserObject" or "object" ? p : null;
            return new
            {
                Id = (string?)(wrapper?.Attribute("id") ?? c.Attribute("id")) ?? string.Empty,
                Label = Clean((string?)(wrapper?.Attribute("label") ?? c.Attribute("value")) ?? string.Empty,
                              html: ((string?)c.Attribute("style") ?? string.Empty).Contains("html=1", StringComparison.Ordinal)),
                Parent = (string?)c.Attribute("parent"),
                Vertex = (string?)c.Attribute("vertex") == "1",
                Edge = (string?)c.Attribute("edge") == "1",
                Source = (string?)c.Attribute("source"),
                Target = (string?)c.Attribute("target"),
            };
        }).ToList();
        var byId = cells.Where(c => c.Id.Length > 0).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var edges = cells.Where(c => c.Edge).Select(c => c.Id).ToHashSet();

        var shapes = new List<string>();
        var unnamed = 0;
        foreach (var c in cells.Where(c => c.Vertex && !(c.Parent is { } p && edges.Contains(p))))
        {
            if (c.Label.Length == 0) { unnamed++; continue; }
            var container = c.Parent is { } parent && byId.TryGetValue(parent, out var holder) && holder.Vertex && holder.Label.Length > 0
                ? $" (in {holder.Label})" : string.Empty;
            shapes.Add(c.Label + container);
        }

        var links = new List<string>();
        foreach (var e in cells.Where(c => c.Edge))
        {
            // An edge's label is its value, or a label cell placed on it.
            var label = e.Label.Length > 0 ? e.Label
                : string.Join(" ", cells.Where(c => c.Vertex && c.Parent == e.Id && c.Label.Length > 0).Select(c => c.Label));
            links.Add(End(e.Source) + " → " + End(e.Target) + (label.Length > 0 ? ": " + label : string.Empty));
        }
        return new(name, shapes, links, unnamed);

        string End(string? id) =>
            id is null ? "(nothing)" : byId.TryGetValue(id, out var c) && c.Label.Length > 0 ? c.Label : $"(shape {id})";
    }

    // ── Visio ───────────────────────────────────────────────────────────────────────

    private static List<Page> Visio(byte[] bytes, CancellationToken ct)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        // ⚠ A page reaches its part through the relationships of pages.xml (its r:id), never by position: pages reordered or
        // deleted keep their part's number, and a name paired by position lands on another page's shapes.
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Part(zip, "visio/pages/_rels/pages.xml.rels") is { } rels)
            foreach (var r in rels.Elements().Where(e => e.Name.LocalName == "Relationship"))
                if ((string?)r.Attribute("Id") is { } id && (string?)r.Attribute("Target") is { Length: > 0 } target)
                    targets[id] = target.StartsWith('/') ? target[1..] : "visio/pages/" + target;
        var listed = new List<(string Name, string Part)>();
        if (Part(zip, "visio/pages/pages.xml") is { } index)
            foreach (var page in index.Elements().Where(e => e.Name.LocalName == "Page"))
            {
                var rel = page.Elements().FirstOrDefault(e => e.Name.LocalName == "Rel")?.Attributes()
                    .FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
                if (rel is not null && targets.TryGetValue(rel, out var part))
                    listed.Add(((string?)page.Attribute("NameU") ?? (string?)page.Attribute("Name") ?? string.Empty, part));
            }
        // Without that index, the page parts themselves, by their number, under a plain name.
        if (listed.Count == 0)
            listed.AddRange(zip.Entries.Select(e => e.FullName)
                .Where(n => n.StartsWith("visio/pages/page", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal)
                            && int.TryParse(n["visio/pages/page".Length..^4], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .OrderBy(n => int.Parse(n["visio/pages/page".Length..^4], CultureInfo.InvariantCulture))
                .Select(n => (string.Empty, n)));

        var pages = new List<Page>();
        for (var i = 0; i < listed.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var contents = Part(zip, listed[i].Part);
            if (contents is null) continue;
            var name = listed[i].Name.Length > 0 ? listed[i].Name : string.Create(CultureInfo.InvariantCulture, $"Page {i + 1}");
            pages.Add(VisioPage(name, contents));
        }
        return pages;
    }

    private static Page VisioPage(string name, XElement contents)
    {
        var shapes = contents.Descendants().Where(e => e.Name.LocalName == "Shape").ToList();
        var textOf = shapes.Where(s => (string?)s.Attribute("ID") is not null).GroupBy(s => (string)s.Attribute("ID")!)
            .ToDictionary(g => g.Key, g => Clean(g.First().Elements().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value ?? string.Empty, html: false));

        // A connector is a shape glued by its two ends: its begin and end name the shapes it links.
        var ends = new Dictionary<string, (string? Begin, string? End)>();
        foreach (var connect in contents.Descendants().Where(e => e.Name.LocalName == "Connect"))
        {
            var from = (string?)connect.Attribute("FromSheet");
            var to = (string?)connect.Attribute("ToSheet");
            if (from is null || to is null) continue;
            ends.TryGetValue(from, out var known);
            ends[from] = (string?)connect.Attribute("FromCell") == "EndX" ? (known.Begin, to) : (to, known.End);
        }

        var list = new List<string>();
        var unnamed = 0;
        foreach (var (id, text) in textOf)
        {
            if (ends.ContainsKey(id)) continue;
            if (text.Length == 0) unnamed++;
            else list.Add(text);
        }
        var links = ends.Select(e => End(e.Value.Begin) + " → " + End(e.Value.End)
                                     + (textOf.GetValueOrDefault(e.Key) is { Length: > 0 } label ? ": " + label : string.Empty)).ToList();
        return new(name, list, links, unnamed);

        string End(string? id) =>
            id is null ? "(nothing)" : textOf.GetValueOrDefault(id) is { Length: > 0 } t ? t : $"(shape {id})";
    }

    private static XElement? Part(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        if (entry.Length > MaxPartBytes) throw new DocumentLimitException("A Visio part past its size limit");
        using var stream = entry.Open();
        return Load(stream);
    }

    private static XElement Load(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XElement.Load(reader);
    }

    /// <summary>A label as text, white space folded; an <paramref name="html"/> one (draw.io's <c>html=1</c>) has its tags
    /// and entities read.</summary>
    /// <remarks>⚠ Only an HTML label: a plain one's angle brackets are its text — <c>List&lt;Order&gt;</c>,
    /// <c>x &lt; 10</c> —, and stripped as tags they vanish.</remarks>
    private static string Clean(string label, bool html)
    {
        if (label.Length == 0) return label;
        return Fold(html ? WebUtility.HtmlDecode(StripTags(label)) : label);
    }

    /// <summary>The label without its tags, in one pass, no pattern: a label comes from the file, and a tag pattern
    /// backtracks on a long run of '&lt;' with no '&gt;'.</summary>
    private static string StripTags(string label)
    {
        var stripped = new StringBuilder(label.Length);
        for (var i = 0; i < label.Length; i++)
        {
            if (label[i] != '<') { stripped.Append(label[i]); continue; }
            var close = label.IndexOf('>', i + 1);
            if (close < 0) { stripped.Append(label, i, label.Length - i); break; }
            var tag = label.AsSpan(i + 1, close - i - 1).TrimStart('/');
            var nameLength = 0;
            while (nameLength < tag.Length && char.IsAsciiLetterOrDigit(tag[nameLength])) nameLength++;
            // A line break or a block ends a word; an inline tag (<b>, <font>) does not.
            if (tag[..nameLength].ToString().ToLowerInvariant() is "br" or "div" or "p" or "li") stripped.Append(' ');
            i = close;
        }
        return stripped.ToString();
    }

    private static string Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!char.IsWhiteSpace(c)) folded.Append(c);
            else if (folded.Length > 0 && folded[^1] != ' ') folded.Append(' ');
        return folded.ToString().Trim();
    }
}
