using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Inferpal.Services.Documents;

/// <summary>A PDF name (<c>/Font</c>), without its slash.</summary>
internal sealed record PdfName(string Value);

/// <summary>A reference to object <paramref name="Number"/> (<c>12 0 R</c>).</summary>
internal sealed record PdfRef(int Number, int Generation);

/// <summary>A PDF string, as its bytes: what they mean depends on the font that shows them.</summary>
internal sealed record PdfString(byte[] Bytes);

/// <summary>A keyword of a content stream (<c>Tj</c>, <c>BT</c>), or a word the object syntax does not know.</summary>
internal sealed record PdfOperator(string Value);

/// <summary>A stream: its dictionary and its raw bytes (still filtered, still encrypted).</summary>
internal sealed class PdfStream(Dictionary<string, object?> dict, ReadOnlyMemory<byte> raw, int number, int generation)
{
    public Dictionary<string, object?> Dict { get; } = dict;
    public ReadOnlyMemory<byte> Raw { get; } = raw;
    public int Number { get; } = number;
    public int Generation { get; } = generation;
}

/// <summary>
/// Reads PDF objects from bytes: one token or object at a time, from a position. Serves both the file's objects and the
/// content streams (where a word that is not <c>true</c>, <c>false</c> or <c>null</c> is an operator).
/// </summary>
internal sealed class PdfLexer(byte[] data, int position = 0)
{
    /// <summary>Arrays and dictionaries nested deeper are refused: read by recursion, a file of a million <c>[</c>
    /// would overflow the stack — an exception no catch can stop, which ends the process.</summary>
    private const int MaxNesting = 64;
    private int _nesting;

    public int Position { get; set; } = position;
    public bool AtEnd => Position >= data.Length;

    private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
                                                   or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public void SkipWhite()
    {
        while (Position < data.Length)
        {
            var b = data[Position];
            if (IsWhite(b)) { Position++; continue; }
            if (b == (byte)'%') { while (Position < data.Length && data[Position] is not (10 or 13)) Position++; continue; }
            break;
        }
    }

    /// <summary>The next object; references (<c>n g R</c>) are read when <paramref name="refs"/>, never in a content
    /// stream (where <c>1 0 0 1 0 0 cm</c> holds no reference). <c>null</c> at the end, or for <c>null</c>.</summary>
    public object? Read(bool refs = true)
    {
        SkipWhite();
        if (AtEnd) return null;
        var b = data[Position];
        switch (b)
        {
            case (byte)'/': return new PdfName(ReadName());
            case (byte)'(': return new PdfString(ReadLiteral());
            case (byte)'<' when Position + 1 < data.Length && data[Position + 1] == (byte)'<': return ReadDict(refs);
            case (byte)'<': return new PdfString(ReadHex());
            case (byte)'[': return ReadArray(refs);
            case (byte)']' or (byte)'>' or (byte)')' or (byte)'}' or (byte)'{':
                Position++;
                return new PdfOperator(((char)b).ToString());
        }
        if (b is (byte)'+' or (byte)'-' or (byte)'.' || b is >= (byte)'0' and <= (byte)'9')
        {
            var number = ReadNumber();
            if (refs && number >= 0 && number == Math.Floor(number))
            {
                var save = Position;
                SkipWhite();
                if (!AtEnd && data[Position] is >= (byte)'0' and <= (byte)'9')
                {
                    var gen = ReadNumber();
                    SkipWhite();
                    if (!AtEnd && data[Position] == (byte)'R' && (Position + 1 >= data.Length || IsWhite(data[Position + 1]) || IsDelimiter(data[Position + 1])))
                    {
                        Position++;
                        return new PdfRef((int)number, (int)gen);
                    }
                }
                Position = save;
            }
            return number;
        }
        var word = ReadWord();
        return word switch
        {
            "true" => true,
            "false" => false,
            "null" => null,
            _ => new PdfOperator(word.Length == 0 ? ((char)data[Position++]).ToString() : word),
        };
    }

    public string ReadWord()
    {
        var start = Position;
        while (Position < data.Length && !IsWhite(data[Position]) && !IsDelimiter(data[Position])) Position++;
        return Encoding.Latin1.GetString(data, start, Position - start);
    }

    private double ReadNumber()
    {
        var start = Position;
        Position++;
        while (Position < data.Length && (data[Position] is >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'-')) Position++;
        var text = Encoding.Latin1.GetString(data, start, Position - start);
        // A malformed number ("--3", "4.-2") reads as 0, as readers do: it never stops the page.
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private string ReadName()
    {
        Position++;
        var sb = new StringBuilder();
        while (Position < data.Length && !IsWhite(data[Position]) && !IsDelimiter(data[Position]))
        {
            if (data[Position] == (byte)'#' && Position + 2 < data.Length && Hex(data[Position + 1]) >= 0 && Hex(data[Position + 2]) >= 0)
            {
                sb.Append((char)(Hex(data[Position + 1]) * 16 + Hex(data[Position + 2])));
                Position += 3;
            }
            else sb.Append((char)data[Position++]);
        }
        return sb.ToString();
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    private byte[] ReadLiteral()
    {
        Position++;
        var bytes = new List<byte>();
        var depth = 1;
        while (Position < data.Length)
        {
            var b = data[Position++];
            if (b == (byte)'\\' && Position < data.Length)
            {
                var e = data[Position++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case 13: if (Position < data.Length && data[Position] == 10) Position++; break;   // line continuation
                    case 10: break;
                    case >= (byte)'0' and <= (byte)'7':
                        var value = e - '0';
                        for (var i = 0; i < 2 && Position < data.Length && data[Position] is >= (byte)'0' and <= (byte)'7'; i++)
                            value = value * 8 + (data[Position++] - '0');
                        bytes.Add((byte)value);
                        break;
                    default: bytes.Add(e); break;
                }
            }
            else if (b == (byte)'(') { depth++; bytes.Add(b); }
            else if (b == (byte)')') { if (--depth == 0) break; bytes.Add(b); }
            else bytes.Add(b);
        }
        return [.. bytes];
    }

    private byte[] ReadHex()
    {
        Position++;
        var bytes = new List<byte>();
        var high = -1;
        while (Position < data.Length && data[Position] != (byte)'>')
        {
            var h = Hex(data[Position++]);
            if (h < 0) continue;
            if (high < 0) high = h;
            else { bytes.Add((byte)(high * 16 + h)); high = -1; }
        }
        if (high >= 0) bytes.Add((byte)(high * 16));
        Position++;
        return [.. bytes];
    }

    private List<object?> ReadArray(bool refs)
    {
        if (++_nesting > MaxNesting) throw new InvalidDataException("PDF arrays nested too deep");
        Position++;
        var list = new List<object?>();
        while (true)
        {
            SkipWhite();
            if (AtEnd) break;
            if (data[Position] == (byte)']') { Position++; break; }
            var before = Position;
            list.Add(Read(refs));
            if (Position == before) Position++;
        }
        _nesting--;
        return list;
    }

    private Dictionary<string, object?> ReadDict(bool refs)
    {
        if (++_nesting > MaxNesting) throw new InvalidDataException("PDF dictionaries nested too deep");
        Position += 2;
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            SkipWhite();
            if (AtEnd) break;
            if (data[Position] == (byte)'>' && Position + 1 < data.Length && data[Position + 1] == (byte)'>') { Position += 2; break; }
            var before = Position;
            if (Read(refs) is PdfName key) dict[key.Value] = Read(refs);
            else if (Position == before) Position++;
        }
        _nesting--;
        return dict;
    }

    /// <summary>The position of <paramref name="word"/> at or after <paramref name="from"/>, or -1.</summary>
    public static int IndexOf(byte[] data, string word, int from)
    {
        var w = Encoding.Latin1.GetBytes(word);
        return data.AsSpan(Math.Min(from, data.Length)).IndexOf(w) is var i and >= 0 ? i + from : -1;
    }
}

/// <summary>
/// The objects of a PDF file, read flat: every <c>n g obj</c> in the bytes (the last one wins: an incremental update comes
/// after what it replaces), then the objects packed in object streams (PDF 1.5+). No cross-reference table is trusted —
/// a damaged one is common, the objects themselves rarely are.
/// </summary>
internal sealed class PdfDocument
{
    /// <summary>The bytes one stream may decode to, and all streams of one document together: a compressed stream can
    /// stand for a thousand times its size (a 20 MB file, 20 GB) — past these, reading stops, said as unreadable.</summary>
    private const long MaxStreamBytes = 64L * 1024 * 1024;
    private const long MaxDecodedBytes = 256L * 1024 * 1024;
    /// <summary>References followed inside one another (a /Length that is a reference to an object whose /Length is…).</summary>
    private const int MaxResolveDepth = 32;

    private readonly byte[] _data;
    private readonly CancellationToken _ct;
    private readonly Dictionary<int, (int Offset, int Generation)> _offsets = [];
    private readonly Dictionary<int, (int Stream, int Index)> _packed = [];
    private readonly Dictionary<int, object?> _cache = [];
    private readonly Dictionary<int, List<(int Number, int Offset)>> _streamIndex = [];
    private readonly Dictionary<int, byte[]> _streamData = [];
    private readonly Dictionary<PdfStream, byte[]> _decoded = new(ReferenceEqualityComparer.Instance);
    private long _decodedBytes;
    private int _getDepth;
    private byte[]? _key;
    private bool _aes;
    private bool _aes256;
    private bool _streamsInClear;   // V4 with /StmF /Identity: strings encrypted, streams not
    private int _encryptObject = -1;

    /// <summary>The deadline of the read: what parses the fonts checks it in its own loops.</summary>
    public CancellationToken Ct => _ct;

    public Dictionary<string, object?> Trailer { get; private set; } = [];

    /// <summary>The file is encrypted and does not open without a password.</summary>
    public bool Locked { get; private set; }

    public PdfDocument(byte[] data, CancellationToken ct)
    {
        _data = data;
        _ct = ct;
        IndexObjects();
        ReadTrailer();
        Unlock();
        IndexPackedObjects();
    }

    private void IndexObjects()
    {
        var at = 0;
        while ((at = PdfLexer.IndexOf(_data, "obj", at)) >= 0)
        {
            _ct.ThrowIfCancellationRequested();
            var end = at + 3;
            // "endobj" contains "obj"; an "obj" keyword stands alone and follows "<number> <number> ".
            if (at >= 3 && _data[at - 1] is (byte)' ' or 10 or 13 && (end >= _data.Length || _data[end] is not ((byte)'e' or (byte)'s')))
            {
                var p = at - 1;
                while (p > 0 && _data[p] is (byte)' ' or 10 or 13) p--;
                var genEnd = p;
                while (p > 0 && _data[p] is >= (byte)'0' and <= (byte)'9') p--;
                var genStart = p + 1;
                while (p > 0 && _data[p] is (byte)' ' or 10 or 13) p--;
                var numEnd = p;
                while (p >= 0 && _data[p] is >= (byte)'0' and <= (byte)'9') p--;
                var numStart = p + 1;
                if (genEnd >= genStart && numEnd >= numStart
                    && int.TryParse(Encoding.Latin1.GetString(_data, numStart, numEnd - numStart + 1), out var number)
                    && int.TryParse(Encoding.Latin1.GetString(_data, genStart, genEnd - genStart + 1), out var generation))
                {
                    _offsets[number] = (end, generation);
                    // Objects do not nest: the search goes on after this one's end, so a "12 0 obj" written inside a
                    // stream (a PDF about PDF, an embedded file) never passes for an object.
                    var close = PdfLexer.IndexOf(_data, "endobj", end);
                    if (close > end) { at = close + 6; continue; }
                }
            }
            at = end;
        }
    }

    private void ReadTrailer()
    {
        // The last "trailer" dictionary, else the last cross-reference stream's dictionary (PDF 1.5+): both name /Root.
        var at = _data.Length;
        for (var tries = 0; tries < 8; tries++)
        {
            var t = LastIndexOf("trailer", at);
            if (t < 0) break;
            if (new PdfLexer(_data, t + 7).Read() is Dictionary<string, object?> d && d.ContainsKey("Root")) { Trailer = d; return; }
            at = t;
        }
        foreach (var number in _offsets.Keys.OrderByDescending(k => _offsets[k].Offset))
        {
            _ct.ThrowIfCancellationRequested();
            if (Get(number) is PdfStream { Dict: var d } && Name(d.GetValueOrDefault("Type")) == "XRef" && d.ContainsKey("Root"))
            {
                Trailer = d;
                return;
            }
        }
        foreach (var number in _offsets.Keys)
            if (!_ct.IsCancellationRequested && Get(number) is Dictionary<string, object?> d && Name(d.GetValueOrDefault("Type")) == "Catalog")
            {
                Trailer = new() { ["Root"] = new PdfRef(number, 0) };
                return;
            }
    }

    private int LastIndexOf(string word, int before)
    {
        var w = Encoding.Latin1.GetBytes(word);
        return _data.AsSpan(0, Math.Min(before, _data.Length)).LastIndexOf(w);
    }

    private void IndexPackedObjects()
    {
        foreach (var number in _offsets.Keys.ToList())
        {
            _ct.ThrowIfCancellationRequested();
            if (Get(number) is not PdfStream { Dict: var d } stream || Name(d.GetValueOrDefault("Type")) != "ObjStm") continue;
            var bytes = Decode(stream);
            var count = (int)Num(d.GetValueOrDefault("N"));
            var first = (int)Num(d.GetValueOrDefault("First"));
            var lexer = new PdfLexer(bytes);
            var index = new List<(int, int)>();
            for (var i = 0; i < count && !lexer.AtEnd; i++)
            {
                var n = lexer.Read(refs: false);
                var offset = lexer.Read(refs: false);
                if (n is double objNum && offset is double off) index.Add(((int)objNum, first + (int)off));
            }
            _streamIndex[number] = index;
            _streamData[number] = bytes;
            for (var i = 0; i < index.Count; i++)
                if (!_offsets.ContainsKey(index[i].Item1)) _packed[index[i].Item1] = (number, i);
        }
    }

    /// <summary>The object numbered <paramref name="number"/>, read once.</summary>
    public object? Get(int number)
    {
        if (_cache.TryGetValue(number, out var known)) return known;
        // A chain of objects read inside one another (a /Length that is a reference to a stream whose /Length is…) is
        // bounded: past it the object reads null, and stays unread for a later caller.
        if (_getDepth >= MaxResolveDepth) return null;
        _cache[number] = null;   // a reference cycle reads null, never loops
        _getDepth++;
        try
        {
            object? value = null;
            if (_offsets.TryGetValue(number, out var at)) value = ReadAt(number, at.Offset, at.Generation);
            else if (_packed.TryGetValue(number, out var packed) && _streamIndex.TryGetValue(packed.Stream, out var index))
                value = new PdfLexer(_streamData[packed.Stream], index[packed.Index].Item2).Read();
            _cache[number] = value;
            return value;
        }
        finally { _getDepth--; }
    }

    private object? ReadAt(int number, int offset, int generation)
    {
        var lexer = new PdfLexer(_data, offset);
        var value = lexer.Read();
        if (value is not Dictionary<string, object?> dict) return value;
        lexer.SkipWhite();
        var save = lexer.Position;
        if (lexer.ReadWord() != "stream") { lexer.Position = save; return value; }
        var start = lexer.Position;
        if (start < _data.Length && _data[start] == 13) start++;
        if (start < _data.Length && _data[start] == 10) start++;
        // The declared length when it holds (it may be a reference, or wrong); else up to "endstream".
        var length = Resolve(dict.GetValueOrDefault("Length")) is double l ? (int)l : -1;
        if (length < 0 || start + length > _data.Length || PdfLexer.IndexOf(_data, "endstream", start + length) is var e && (e < 0 || e - (start + length) > 4))
        {
            var end = PdfLexer.IndexOf(_data, "endstream", start);
            length = end < 0 ? _data.Length - start : end - start;
            while (length > 0 && _data[start + length - 1] is 10 or 13) length--;
        }
        // A window on the file, never a copy: streams without an end would each copy the rest of the file.
        return new PdfStream(dict, _data.AsMemory(start, Math.Max(0, length)), number, generation);
    }

    /// <summary>A value with its reference followed (once: a reference to a reference is followed again).</summary>
    public object? Resolve(object? value)
    {
        for (var i = 0; i < 8 && value is PdfRef r; i++) value = Get(r.Number);
        return value;
    }

    public Dictionary<string, object?>? Dict(object? value) => Resolve(value) switch
    {
        Dictionary<string, object?> d => d,
        PdfStream s => s.Dict,
        _ => null,
    };

    public static string? Name(object? value) => value is PdfName n ? n.Value : null;
    public double Num(object? value) => Resolve(value) is double d ? d : 0;

    // ── Decoding a stream: decryption, then its filters ──────────────────────────────────────────────────────────

    /// <summary>The bytes of <paramref name="stream"/> decrypted and unfiltered; what a filter here does not know stays as
    /// it is (an image), and a stream cut short gives what it holds.</summary>
    public byte[] Decode(PdfStream stream)
    {
        // Decoded once: a content stream shared by every page, or a form drawn again and again, is not inflated each time.
        if (_decoded.TryGetValue(stream, out var known)) return known;
        var bytes = stream.Raw.ToArray();
        if (_key is not null && !_streamsInClear && stream.Number != _encryptObject && Name(stream.Dict.GetValueOrDefault("Type")) != "XRef")
            bytes = Decrypt(bytes, stream.Number, stream.Generation);
        var filters = Resolve(stream.Dict.GetValueOrDefault("Filter")) switch
        {
            PdfName n => [n.Value],
            List<object?> list => list.Select(f => Name(Resolve(f)) ?? string.Empty).ToList(),
            _ => new List<string>(),
        };
        foreach (var filter in filters)
        {
            _ct.ThrowIfCancellationRequested();
            bytes = filter switch
            {
                "FlateDecode" or "Fl" => Inflate(bytes),
                "ASCIIHexDecode" or "AHx" => FromHex(bytes),
                "ASCII85Decode" or "A85" => FromAscii85(bytes),
                _ => bytes,
            };
            if (filter is not ("FlateDecode" or "Fl" or "ASCIIHexDecode" or "AHx" or "ASCII85Decode" or "A85")) break;
        }
        _decodedBytes += bytes.Length;
        if (_decodedBytes > MaxDecodedBytes) throw new DocumentLimitException("PDF streams decode past the document's budget");
        _decoded[stream] = bytes;
        return bytes;
    }

    /// <summary>A Flate stream inflated within <see cref="MaxStreamBytes"/> and the read's deadline: a compressed bomb
    /// stops there (said as unreadable), never fills the memory.</summary>
    private byte[] Inflate(byte[] bytes)
    {
        var output = new MemoryStream();
        try { CopyBounded(new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress), output); }
        catch (InvalidDataException ex)
        {
            // A stream cut short, or deflate data without the zlib header: what was read is kept, else read again raw.
            Diagnostics.Swallow("PdfDocument.Inflate", ex);
            if (output.Length == 0 && bytes.Length > 2)
            {
                try { CopyBounded(new DeflateStream(new MemoryStream(bytes, 2, bytes.Length - 2), CompressionMode.Decompress), output); }
                catch (InvalidDataException raw) { Diagnostics.Swallow("PdfDocument.InflateRaw", raw); }
            }
        }
        return output.ToArray();
    }

    private void CopyBounded(Stream source, MemoryStream output)
    {
        using (source)
        {
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                _ct.ThrowIfCancellationRequested();
                if (output.Length + read > MaxStreamBytes) throw new DocumentLimitException("A PDF stream decodes past its size limit");
                output.Write(buffer, 0, read);
            }
        }
    }

    private static byte[] FromHex(byte[] bytes) =>
        new PdfLexer([(byte)'<', .. bytes.TakeWhile(b => b != (byte)'>'), (byte)'>']).Read() is PdfString s ? s.Bytes : [];

    private static byte[] FromAscii85(byte[] bytes)
    {
        var output = new List<byte>();
        var group = new List<int>();
        foreach (var b in bytes)
        {
            if (b == (byte)'~') break;
            if (b == (byte)'z' && group.Count == 0) { output.AddRange([0, 0, 0, 0]); continue; }
            if (b is < 33 or > 117) continue;
            group.Add(b - 33);
            if (group.Count == 5) { Emit(5); group.Clear(); }
        }
        if (group.Count > 1) { var n = group.Count; while (group.Count < 5) group.Add(84); Emit(n); }
        return [.. output];

        void Emit(int n)
        {
            long v = 0;
            foreach (var g in group) v = v * 85 + g;
            for (var i = 0; i < n - 1; i++) output.Add((byte)(v >> (24 - 8 * i)));
        }
    }

    // ── Encryption: the standard handler, opened with the empty user password ────────────────────────────────────

    private static readonly byte[] Pad =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    /// <summary>
    /// A file encrypted with only an owner password (it opens without one: a form, a report that forbids printing) is read
    /// with the empty user password, as every viewer does; one that asks for a password to open stays <see cref="Locked"/>.
    /// </summary>
    private void Unlock()
    {
        if (Trailer.GetValueOrDefault("Encrypt") is not { } encryptValue) return;
        if (encryptValue is PdfRef er) _encryptObject = er.Number;
        var enc = Dict(encryptValue);
        if (enc is null || Name(enc.GetValueOrDefault("Filter")) != "Standard") { Locked = true; return; }
        var v = (int)Num(enc.GetValueOrDefault("V"));
        var r = (int)Num(enc.GetValueOrDefault("R"));
        var o = (Resolve(enc.GetValueOrDefault("O")) as PdfString)?.Bytes ?? [];
        var u = (Resolve(enc.GetValueOrDefault("U")) as PdfString)?.Bytes ?? [];
        if (r >= 5)
        {
            UnlockAes256(enc, r, u);
            return;
        }
        var p = (int)(long)Num(enc.GetValueOrDefault("P"));
        var id = Resolve(Trailer.GetValueOrDefault("ID")) is List<object?> ids && ids.Count > 0 && Resolve(ids[0]) is PdfString s ? s.Bytes : [];
        var length = r == 2 ? 5 : Math.Clamp((int)(Num(enc.GetValueOrDefault("Length")) is > 0 and var bits ? bits : 128) / 8, 5, 16);
        if (v == 4)
        {
            var cf = Dict(enc.GetValueOrDefault("CF"));
            var stmF = Name(enc.GetValueOrDefault("StmF")) ?? "Identity";
            _streamsInClear = stmF == "Identity";   // only the strings are encrypted: streams are read as they are
            var std = Dict(cf?.GetValueOrDefault(stmF));
            _aes = Name(std?.GetValueOrDefault("CFM")) == "AESV2";
            length = 16;
        }
        var md5Input = new List<byte>(Pad);
        md5Input.AddRange(o.Take(32));
        md5Input.AddRange(BitConverter.GetBytes(p));
        md5Input.AddRange(id);
        if (r >= 4 && enc.GetValueOrDefault("EncryptMetadata") is false) md5Input.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        var key = MD5.HashData([.. md5Input]);
        if (r >= 3) for (var i = 0; i < 50; i++) key = MD5.HashData(key.AsSpan(0, length));
        key = key[..length];

        // The empty password opens it when the computed U matches the file's.
        bool opens;
        if (r == 2) opens = Rc4(key, Pad).AsSpan().SequenceEqual(u.AsSpan(0, Math.Min(32, u.Length)));
        else
        {
            var check = Rc4(key, MD5.HashData([.. Pad, .. id]));
            for (var i = 1; i <= 19; i++) check = Rc4(key.Select(k => (byte)(k ^ i)).ToArray(), check);
            opens = u.Length >= 16 && check.AsSpan(0, 16).SequenceEqual(u.AsSpan(0, 16));
        }
        if (opens) _key = key;
        else Locked = true;
    }

    private void UnlockAes256(Dictionary<string, object?> enc, int r, byte[] u)
    {
        var ue = (Resolve(enc.GetValueOrDefault("UE")) as PdfString)?.Bytes ?? [];
        if (u.Length < 48 || ue.Length < 32) { Locked = true; return; }
        var hash = Hash2B([], u.AsSpan(32, 8).ToArray(), [], r);
        if (!hash.AsSpan(0, 32).SequenceEqual(u.AsSpan(0, 32))) { Locked = true; return; }
        var keyKey = Hash2B([], u.AsSpan(40, 8).ToArray(), [], r);
        using var aes = Aes.Create();
        aes.Key = keyKey[..32];
        _key = aes.DecryptCbc(ue.AsSpan(0, 32), new byte[16], PaddingMode.None);
        _aes = true;
        _aes256 = true;
    }

    /// <summary>The hash of an AES-256 file's password (algorithm 2.B of ISO 32000-2; plain SHA-256 for revision 5).</summary>
    private static byte[] Hash2B(byte[] password, byte[] salt, byte[] userKey, int r)
    {
        var k = SHA256.HashData([.. password, .. salt, .. userKey]);
        if (r == 5) return k;
        for (var round = 0; ; round++)
        {
            var k1 = new List<byte>();
            for (var i = 0; i < 64; i++) { k1.AddRange(password); k1.AddRange(k); k1.AddRange(userKey); }
            using var aes = Aes.Create();
            aes.Key = k[..16];
            var e = aes.EncryptCbc(k1.ToArray(), k.AsSpan(16, 16), PaddingMode.None);
            var mod = 0;
            for (var i = 0; i < 16; i++) mod += e[i];
            k = (mod % 3) switch { 0 => SHA256.HashData(e), 1 => SHA384.HashData(e), _ => SHA512.HashData(e) };
            // The round counts from 1 once this one is done (as pdf.js and qpdf count it): at least 64 rounds, then until
            // the last byte of E is at most that count minus 32.
            if (round + 1 >= 64 && e[^1] <= round + 1 - 32) return k[..32];
        }
    }

    private byte[] Decrypt(byte[] bytes, int number, int generation)
    {
        byte[] key;
        if (_aes256) key = _key!;
        else
        {
            var input = new List<byte>(_key!);
            input.AddRange([(byte)number, (byte)(number >> 8), (byte)(number >> 16), (byte)generation, (byte)(generation >> 8)]);
            if (_aes) input.AddRange("sAlT"u8.ToArray());
            key = MD5.HashData([.. input])[..Math.Min(16, _key!.Length + 5)];
        }
        if (!_aes) return Rc4(key, bytes);
        if (bytes.Length < 32 || bytes.Length % 16 != 0) return [];
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(bytes.AsSpan(16), bytes.AsSpan(0, 16), PaddingMode.PKCS7);
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 255;
            j = (j + s[i]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 255]);
        }
        return output;
    }
}
