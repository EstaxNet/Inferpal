using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Inferpal.Services.Documents;

/// <summary>
/// A Windows event log (<c>.evtx</c>) read as a text log, on every system: a log copied from a server reads on a Mac. Each
/// event becomes one entry — <c>yyyy-MM-dd HH:mm:ss+zz:zz LEVEL Provider[Id]: data</c>, the lines under it indented — so
/// <c>read_file</c>'s level and time filters (<c>LogFilter</c>) read it as they read any log.
/// </summary>
/// <remarks>
/// The format: a 4 KiB file header, then 64 KiB chunks of records; each record holds its event in binary XML, mostly as an
/// instance of a template defined once in its chunk, with the event's values substituted. ⚠ The message Windows shows is
/// not in the file: it lives in the provider's message DLL, absent off the machine — the event's data is shown as written
/// (for a .NET crash, the exception and its stack whole). ⚠ A damaged chunk or record is counted and said, never a read
/// that stops in silence: a log copied while Windows wrote it (a "dirty" log) is common.
/// </remarks>
internal static class EvtxText
{
    private const int FileHeaderSize = 4096;
    private const int ChunkSize = 65536;
    private const int ChunkHeaderSize = 512;

    /// <summary>The text of the event log in <paramref name="bytes"/>; its first line says what it is.</summary>
    /// <param name="maxChars">The text kept: past it, the OLDEST events are left out and said — in a log the last events
    /// are the ones looked for, and the level and time filters read only what is kept.</param>
    internal static DocumentExtraction Extract(byte[] bytes, CancellationToken ct, int maxChars = PdfText.MaxChars)
    {
        if (bytes.Length < FileHeaderSize || !bytes.AsSpan(0, 8).SequenceEqual("ElfFile\0"u8))
            throw new InvalidDataException("not a Windows event log");

        var kept = new Queue<string>();
        long keptChars = 0;
        int events = 0, damaged = 0, dropped = 0;

        // ⚠ A full log wraps around: its oldest chunk can sit anywhere in the file. Chunks are read in the order of their
        // first record's identifier, never in file order — read in file order, "the newest kept" would keep the oldest.
        var chunks = new List<(ulong First, int At)>();
        var chunk = FileHeaderSize;
        for (; chunk + ChunkSize <= bytes.Length; chunk += ChunkSize)
        {
            if (bytes.AsSpan(chunk, 8).SequenceEqual("ElfChnk\0"u8)) chunks.Add((U64(bytes, chunk + 24), chunk));
            // A chunk never written is zeros; anything else is a damaged one.
            else if (bytes.AsSpan(chunk, ChunkSize).IndexOfAnyExcept((byte)0) >= 0) damaged++;
        }
        // A file cut inside a chunk (copied while Windows wrote it, a download that stopped): that part is damaged, said.
        if (chunk < bytes.Length && bytes.AsSpan(chunk).IndexOfAnyExcept((byte)0) >= 0) damaged++;

        foreach (var (_, start) in chunks.OrderBy(c => c.First))
        {
            ct.ThrowIfCancellationRequested();
            var reader = new ChunkReader(bytes, start);
            var free = (int)Math.Min(U32(bytes, start + 48), ChunkSize);
            // The first record expected is the one the chunk header names: a header that counts no record yet still has one.
            ulong? next = U64(bytes, start + 24);
            for (var at = ChunkHeaderSize; at + 28 <= ChunkSize;)
            {
                ct.ThrowIfCancellationRequested();
                var record = start + at;
                // Past the free-space offset, a record is read only when it follows the last one read: a log copied while
                // Windows wrote it holds records its chunk header does not count yet, and a reused chunk holds older ones.
                var pastFree = at >= free;
                if (U32(bytes, record) != 0x00002a2a) { if (!pastFree) damaged++; break; }
                var size = (int)U32(bytes, record + 4);
                if (size < 28 || at + size > ChunkSize) { if (!pastFree) damaged++; break; }
                var id = U64(bytes, record + 8);
                if (pastFree && id != next) break;
                next = id + 1;
                try
                {
                    var written = DateTime.FromFileTimeUtc((long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(record + 16)));
                    var entry = Entry(reader.Event(record + 24, record + size - 4), written);
                    events++;
                    kept.Enqueue(entry);
                    keptChars += entry.Length;
                    while (keptChars > maxChars && kept.Count > 0) { keptChars -= kept.Dequeue().Length; dropped++; }
                }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException
                                               or OverflowException or KeyNotFoundException)
                {
                    Diagnostics.Swallow("EvtxText.Record", ex);
                    damaged++;
                }
                at += size;
            }
        }

        if (events == 0 && damaged == 0) return new(string.Empty, 0, 0, DocumentProblem.NoText);
        var header = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"[Windows event log: {events} event(s), oldest first, times in this machine's time zone. The message Windows shows lives in each provider's DLL and is not in the file (a forwarded log keeps it, and it is shown): each event's data is shown as written."));
        if (damaged > 0) header.Append(CultureInfo.InvariantCulture, $" {damaged} damaged record(s) or chunk(s) could not be read.");
        if (dropped > 0)
            header.Append(CultureInfo.InvariantCulture, $" The {dropped} oldest event(s) do not fit and are left out: the newest {kept.Count} are here.");
        header.Append(']');
        return new(string.Concat(kept), 0, 0, DocumentProblem.None, dropped > 0 ? 1 : 0, header.ToString());
    }

    /// <summary>One event as a log entry: its time, level and source on the first line, its data under it.</summary>
    private static string Entry(Element? ev, DateTime writtenUtc)
    {
        var system = ev?.Child("System");
        var provider = system?.Child("Provider")?.Attr("Name") ?? system?.Child("Provider")?.Attr("EventSourceName") ?? "?";
        var id = system?.Child("EventID")?.Text() ?? "?";
        var level = (system?.Child("Level")?.Text()) switch
        {
            "1" => "CRITICAL",
            "2" => "ERROR",
            "3" => "WARNING",
            "5" => "VERBOSE",
            _   => "INFO",
        };
        var data = new List<string>();
        var length = 0;
        void Add(string line)
        {
            if (length > MaxEventChars) return;
            length += line.Length;
            data.Add(length > MaxEventChars ? line[..Math.Max(0, line.Length - (length - MaxEventChars))] + " … (event data cut)" : line);
        }
        // A forwarded log keeps the message Windows rendered on the source machine: it says more than the raw data.
        if (ev?.Child("RenderingInfo")?.Child("Message")?.Text() is { Length: > 0 } message) Add(message);
        if (ev?.Child("EventData") is { } eventData)
        {
            foreach (var d in eventData.Children.OfType<Element>())
            {
                var value = d.Text();
                if (value.Length == 0) continue;
                Add(d.Attr("Name") is { Length: > 0 } name ? name + ": " + value : value);
            }
        }
        else if (ev?.Child("UserData")?.Children.OfType<Element>().FirstOrDefault() is { } userData)
        {
            foreach (var d in userData.Children.OfType<Element>())
                if (d.Text() is { Length: > 0 } value) Add(d.Name + ": " + value);
        }
        return EntryText(writtenUtc, level, provider, id, data);
    }

    /// <summary>An entry as the log filter reads one: its time in this machine's zone and its level on the first line,
    /// its data under it.</summary>
    internal static string EntryText(DateTime writtenUtc, string level, string provider, string id, IEnumerable<string> data)
    {
        var local = DateTime.SpecifyKind(writtenUtc, DateTimeKind.Utc).ToLocalTime();
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var stamp = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString("hh\\:mm", CultureInfo.InvariantCulture);
        // ⚠ Every line under the first is indented: a line of data that opens with a time or a level word would be taken
        // for an entry of its own by the log filter.
        var lines = string.Join("\n", data).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Where(l => l.Trim().Length > 0).ToList();
        var sb = new StringBuilder();
        sb.Append(stamp).Append(' ').Append(level).Append(' ').Append(provider).Append('[').Append(id).Append(']');
        if (lines.Count > 0) sb.Append(": ").Append(lines[0].Trim());
        sb.Append('\n');
        foreach (var line in lines.Skip(1)) sb.Append("    ").Append(line.TrimEnd()).Append('\n');
        return sb.ToString();
    }

    /// <summary>The most text one value may give, then one event's data: a .NET crash's stack fits in the first.</summary>
    private const int MaxValueChars = 32_000, MaxEventChars = 64_000;

    /// <summary>The most nodes one value's text may visit.</summary>
    private const int MaxVisits = 100_000;

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));

    private static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at, 8));

    // ── Binary XML ──────────────────────────────────────────────────────────────────

    private abstract class Node;

    private sealed class TextNode(string text) : Node { public string Value => text; }

    /// <summary>A place in a template that the instance's value <c>Index</c> fills; an optional one with no value leaves
    /// nothing.</summary>
    private sealed class Substitution(int index) : Node { public int Index => index; }

    private sealed class Element(string name) : Node
    {
        public string Name => name;
        public List<(string Name, List<Node> Value)> Attributes { get; } = [];
        public List<Node> Children { get; } = [];

        public Element? Child(string n) => Children.OfType<Element>().FirstOrDefault(e => e.Name == n);
        public string? Attr(string n) => Attributes.Where(a => a.Name == n).Select(a => Flatten(a.Value)).FirstOrDefault();
        public string Text() => Flatten(Children);

        /// <summary>The text of <paramref name="nodes"/>, within <see cref="MaxValueChars"/> and <see cref="MaxVisits"/>.</summary>
        /// <remarks>⚠ Bounded in what it builds, not only in depth: a value can be referenced many times, nested, and its
        /// text is rebuilt for each reference — a few hundred bytes of a crafted record expand to gigabytes.</remarks>
        private static string Flatten(List<Node> nodes)
        {
            var sb = new StringBuilder();
            var visits = 0;
            if (!Append(nodes, sb, ref visits)) sb.Append(" … (value cut)");
            return sb.ToString();
        }

        /// <summary>False once the budget ran out (the text is cut there).</summary>
        private static bool Append(List<Node> nodes, StringBuilder sb, ref int visits)
        {
            foreach (var node in nodes)
            {
                if (++visits > MaxVisits) return false;
                if (node is TextNode t) sb.Append(t.Value);
                else if (node is Element e && !Append(e.Children, sb, ref visits)) return false;
                if (sb.Length > MaxValueChars) { sb.Length = MaxValueChars; return false; }
            }
            return true;
        }
    }

    /// <summary>Reads the binary XML of one chunk: names and templates are defined once and referenced by their offset in
    /// it.</summary>
    private sealed class ChunkReader(byte[] bytes, int chunk)
    {
        private readonly Dictionary<int, List<Node>> _templates = [];

        /// <summary>The event element of the record whose binary XML lies between <paramref name="start"/> and
        /// <paramref name="end"/>.</summary>
        public Element? Event(int start, int end)
        {
            var p = start;
            return Content(ref p, end).OfType<Element>().FirstOrDefault();
        }

        private int Rel(int p) => p - chunk;

        private ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));

        private uint U32At(int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));

        private string Utf16(int at, int byteCount) => Encoding.Unicode.GetString(bytes, at, byteCount);

        /// <summary>A name defined at <paramref name="offset"/> in the chunk; read past when it is defined right here.</summary>
        private string Name(uint offset, ref int p)
        {
            var at = chunk + (int)offset;
            if (offset >= ChunkSize) throw new InvalidDataException("name outside its chunk");
            var count = U16(at + 6);
            var name = Utf16(at + 8, count * 2);
            if (offset == Rel(p)) p += 8 + count * 2 + 2;
            return name;
        }

        /// <summary>How deep elements, templates and nested values may go.</summary>
        private const int MaxDepth = 64;

        private int _depth;

        /// <summary>Nodes up to the end of the fragment or of the element they belong to; a substitution stays a placeholder
        /// that <see cref="Fill"/> replaces.</summary>
        /// <remarks>⚠ Every recursion passes here, bounded: a damaged or crafted log whose template names itself, or that
        /// nests without end, would overflow the stack — which no catch stops, and which takes the editor's host down.</remarks>
        private List<Node> Content(ref int p, int end)
        {
            _depth++;
            try
            {
                if (_depth > MaxDepth) throw new InvalidDataException("binary XML nested too deep");
                return ContentWithin(ref p, end);
            }
            finally { _depth--; }
        }

        private List<Node> ContentWithin(ref int p, int end)
        {
            var nodes = new List<Node>();
            while (p < end)
            {
                var token = bytes[p] & ~0x40;
                switch (token)
                {
                    case 0x00:                                   // end of the fragment
                    case 0x04:                                   // end of the element
                        p++;
                        return nodes;
                    case 0x0F:                                   // fragment header
                        p += 4;
                        break;
                    case 0x01:
                        nodes.Add(OpenElement(ref p, end));
                        break;
                    case 0x0C:
                        nodes.AddRange(TemplateInstance(ref p));
                        // An instance is a whole fragment: its values follow it, and nothing else.
                        return nodes;
                    default:
                        if (!Value(ref p, nodes)) throw new InvalidDataException($"unexpected token 0x{bytes[p]:x2}");
                        break;
                }
            }
            return nodes;
        }

        /// <summary>A value, a substitution, a character or an entity: added to <paramref name="into"/>; false for any other
        /// token (not read).</summary>
        private bool Value(ref int p, List<Node> into)
        {
            switch (bytes[p] & ~0x40)
            {
                case 0x05:                                       // a value written in the template: a string
                {
                    var type = bytes[p + 1];
                    p += 2;
                    if (type != 0x01) throw new InvalidDataException($"value of type 0x{type:x2} in a template");
                    var count = U16(p);
                    into.Add(new TextNode(Utf16(p + 2, count * 2)));
                    p += 2 + count * 2;
                    return true;
                }
                case 0x0D:                                       // substitution
                case 0x0E:                                       // optional substitution
                {
                    into.Add(new Substitution(U16(p + 1)));
                    p += 4;
                    return true;
                }
                case 0x07:                                       // CDATA
                {
                    var count = U16(p + 1);
                    into.Add(new TextNode(Utf16(p + 3, count * 2)));
                    p += 3 + count * 2;
                    return true;
                }
                case 0x08:                                       // character reference
                    into.Add(new TextNode(((char)U16(p + 1)).ToString()));
                    p += 3;
                    return true;
                case 0x09:                                       // entity reference
                {
                    p++;
                    var offset = U32At(p);
                    p += 4;
                    into.Add(new TextNode(Name(offset, ref p) switch
                    {
                        "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'", var other => "&" + other + ";",
                    }));
                    return true;
                }
                case 0x0A:                                       // processing instruction: not shown
                {
                    p++;
                    var offset = U32At(p);
                    p += 4;
                    Name(offset, ref p);
                    return true;
                }
                case 0x0B:
                    p += 3 + U16(p + 1) * 2;
                    return true;
                default:
                    return false;
            }
        }

        private Element OpenElement(ref int p, int end)
        {
            var hasAttributes = (bytes[p] & 0x40) != 0;
            p += 1 + 2 + 4;                                      // token, dependency id, data size
            var nameOffset = U32At(p);
            p += 4;
            var element = new Element(Name(nameOffset, ref p));
            if (hasAttributes) p += 4;                           // attribute list size
            while ((bytes[p] & ~0x40) == 0x06)
            {
                p++;
                var attributeOffset = U32At(p);
                p += 4;
                var name = Name(attributeOffset, ref p);
                var value = new List<Node>();
                while (Value(ref p, value)) { }
                element.Attributes.Add((name, value));
            }
            switch (bytes[p] & ~0x40)
            {
                case 0x02:                                       // children follow, up to the element's end
                    p++;
                    element.Children.AddRange(Content(ref p, end));
                    break;
                case 0x03:                                       // empty element
                    p++;
                    break;
                default:
                    throw new InvalidDataException($"element {element.Name} not closed (0x{bytes[p]:x2})");
            }
            return element;
        }

        /// <summary>A template instance: the template (defined here or earlier in the chunk) with this record's values.</summary>
        private List<Node> TemplateInstance(ref int p)
        {
            var start = p;
            p += 2;                                              // token, unknown byte
            p += 4;                                              // template id
            var definition = U32At(p);
            p += 4;
            if (definition > ChunkSize - 24) throw new InvalidDataException("template outside its chunk");
            var defined = chunk + (int)definition;
            var dataSize = (int)Math.Min(U32At(defined + 20), (uint)(ChunkSize - (int)definition - 24));
            if (!_templates.TryGetValue((int)definition, out var template))
            {
                var q = defined + 24;
                template = Content(ref q, defined + 24 + dataSize);
                _templates[(int)definition] = template;
            }
            if ((int)definition > Rel(start)) p = defined + 24 + dataSize;   // defined right here: read past it

            var count = (int)U32At(p);
            p += 4;
            if (count > 4096) throw new InvalidDataException("too many values");
            var declared = new (int Size, byte Type)[count];
            for (var i = 0; i < count; i++) { declared[i] = (U16(p), bytes[p + 2]); p += 4; }
            var values = new List<List<Node>>(count);
            foreach (var (size, type) in declared)
            {
                values.Add(ValueNodes(p, size, type));
                p += size;
            }
            return Fill(template, values);
        }

        /// <summary>The template's nodes with each substitution replaced by its value.</summary>
        private static List<Node> Fill(List<Node> template, List<List<Node>> values)
        {
            var filled = new List<Node>();
            foreach (var node in template)
            {
                switch (node)
                {
                    case Substitution s:
                        if (s.Index < values.Count) filled.AddRange(values[s.Index]);
                        break;
                    case Element e:
                        var copy = new Element(e.Name);
                        foreach (var (name, value) in e.Attributes) copy.Attributes.Add((name, Fill(value, values)));
                        copy.Children.AddRange(Fill(e.Children, values));
                        filled.Add(copy);
                        break;
                    default:
                        filled.Add(node);
                        break;
                }
            }
            return filled;
        }

        /// <summary>A substitution value as nodes: text, or the elements of a nested binary XML fragment.</summary>
        private List<Node> ValueNodes(int at, int size, byte type)
        {
            if (size == 0 || type == 0x00) return [];
            if (type == 0x21)                                    // binary XML
            {
                var q = at;
                return Content(ref q, at + size);
            }
            return [new TextNode(ValueText(bytes.AsSpan(at, size), type))];
        }

        private static string ValueText(ReadOnlySpan<byte> v, byte type)
        {
            var inv = CultureInfo.InvariantCulture;
            if ((type & 0x80) != 0)                              // an array
            {
                if ((type & 0x7F) == 0x01)
                    return string.Join(", ", Encoding.Unicode.GetString(v).Split('\0', StringSplitOptions.RemoveEmptyEntries));
                var width = Width((byte)(type & 0x7F));
                if (width == 0) return Convert.ToHexString(v);
                var items = new List<string>();
                for (var i = 0; i + width <= v.Length; i += width) items.Add(ValueText(v.Slice(i, width), (byte)(type & 0x7F)));
                return string.Join(", ", items);
            }
            return type switch
            {
                0x01 => Encoding.Unicode.GetString(v).TrimEnd('\0'),
                0x02 => Encoding.Latin1.GetString(v).TrimEnd('\0'),
                0x03 => ((sbyte)v[0]).ToString(inv),
                0x04 => v[0].ToString(inv),
                0x05 => BinaryPrimitives.ReadInt16LittleEndian(v).ToString(inv),
                0x06 => BinaryPrimitives.ReadUInt16LittleEndian(v).ToString(inv),
                0x07 => BinaryPrimitives.ReadInt32LittleEndian(v).ToString(inv),
                0x08 => BinaryPrimitives.ReadUInt32LittleEndian(v).ToString(inv),
                0x09 => BinaryPrimitives.ReadInt64LittleEndian(v).ToString(inv),
                0x0A => BinaryPrimitives.ReadUInt64LittleEndian(v).ToString(inv),
                0x0B => BinaryPrimitives.ReadSingleLittleEndian(v).ToString(inv),
                0x0C => BinaryPrimitives.ReadDoubleLittleEndian(v).ToString(inv),
                0x0D => (BinaryPrimitives.ReadUInt32LittleEndian(v) != 0).ToString(inv).ToLowerInvariant(),
                0x0F when v.Length >= 16 => new Guid(v[..16]).ToString("B", inv),
                // Hexadecimal as Windows writes it: no leading zeros.
                0x10 => "0x" + (v.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(v).ToString("x", inv)
                                              : BinaryPrimitives.ReadUInt32LittleEndian(v).ToString("x", inv)),
                0x11 => FileTime(v),
                0x12 when v.Length >= 16 => SystemTime(v),
                0x13 => Sid(v),
                0x14 => "0x" + BinaryPrimitives.ReadUInt32LittleEndian(v).ToString("x", inv),
                0x15 => "0x" + BinaryPrimitives.ReadUInt64LittleEndian(v).ToString("x", inv),
                _    => Convert.ToHexString(v),
            };
        }

        // A date that is not one (past the calendar, a zero month) is shown as its bytes: one bad value never costs the event.
        private static string FileTime(ReadOnlySpan<byte> v)
        {
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(v);
            return ticks >= 0 && ticks <= DateTime.MaxValue.ToFileTimeUtc()
                ? DateTime.FromFileTimeUtc(ticks).ToString("o", CultureInfo.InvariantCulture) : "0x" + Convert.ToHexString(v);
        }

        private static string SystemTime(ReadOnlySpan<byte> v)
        {
            // Year, month, day of the week, day, hour, minute, second, millisecond.
            var f = new int[8];
            for (var i = 0; i < 8; i++) f[i] = BinaryPrimitives.ReadUInt16LittleEndian(v[(i * 2)..]);
            try
            {
                return new DateTime(f[0], f[1], f[3], f[4], f[5], f[6], f[7], DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException) { return "0x" + Convert.ToHexString(v[..16]); }
        }

        private static int Width(byte type) => type switch
        {
            0x03 or 0x04 => 1,
            0x05 or 0x06 => 2,
            0x07 or 0x08 or 0x0B or 0x0D or 0x14 => 4,
            0x09 or 0x0A or 0x0C or 0x11 or 0x15 => 8,
            0x0F or 0x12 => 16,
            _ => 0,
        };

        private static string Sid(ReadOnlySpan<byte> v)
        {
            if (v.Length < 8) return Convert.ToHexString(v);
            var authority = 0UL;
            for (var i = 2; i < 8; i++) authority = (authority << 8) | v[i];
            var sb = new StringBuilder("S-").Append(v[0]).Append('-').Append(authority);
            for (var i = 0; i < v[1] && 8 + i * 4 + 4 <= v.Length; i++)
                sb.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(v[(8 + i * 4)..]));
            return sb.ToString();
        }
    }
}
