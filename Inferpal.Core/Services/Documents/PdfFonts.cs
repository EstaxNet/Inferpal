using System.Text;

namespace Inferpal.Services.Documents;

/// <summary>
/// What a PDF font says about its codes: how many bytes a code takes, the characters it stands for, and its width (for the
/// spaces a PDF draws by moving the pen instead of writing one). Read from <c>/ToUnicode</c> first — the only map a
/// composite font has —, else from the simple font's encoding and its <c>/Differences</c>.
/// </summary>
internal sealed class PdfFont
{
    private readonly Dictionary<int, string> _unicode = [];
    private readonly Dictionary<int, double> _widths = [];
    private readonly List<(int Length, int Low, int High)> _codespace = [];
    private double _defaultWidth = 1000;
    private double _scale = 0.001;   // glyph units to text space: 1/1000, or a Type 3 font's own matrix

    /// <summary>A composite font (Type 0): codes of one or more bytes, characters only through its ToUnicode map.</summary>
    public bool Composite { get; }

    /// <summary>Whether this font can give characters at all: a composite font without ToUnicode gives none.</summary>
    public bool HasText { get; }

    public PdfFont(PdfDocument doc, Dictionary<string, object?>? dict)
    {
        dict ??= [];
        Composite = PdfDocument.Name(dict.GetValueOrDefault("Subtype")) == "Type0";
        if (doc.Resolve(dict.GetValueOrDefault("ToUnicode")) is PdfStream toUnicode) ReadCMap(Encoding.Latin1.GetString(doc.Decode(toUnicode)), doc.Ct);

        if (Composite)
        {
            var descendant = doc.Resolve(dict.GetValueOrDefault("DescendantFonts")) is List<object?> list && list.Count > 0 ? doc.Dict(list[0]) : null;
            _defaultWidth = descendant?.GetValueOrDefault("DW") is { } dw ? doc.Num(dw) : 1000;
            if (doc.Resolve(descendant?.GetValueOrDefault("W")) is List<object?> w) ReadCidWidths(doc, w);
            // ⚠ The code length is the font's encoding's: Identity-H/V are two bytes whatever the ToUnicode map declares;
            // the map's codespace serves only another CMap.
            if (PdfDocument.Name(doc.Resolve(dict.GetValueOrDefault("Encoding"))) is "Identity-H" or "Identity-V" || _cmapCodespace.Count == 0)
                _codespace.Add((2, 0, 0xFFFF));
            else _codespace.AddRange(_cmapCodespace);
            HasText = _unicode.Count > 0;
            return;
        }

        if (PdfDocument.Name(dict.GetValueOrDefault("Subtype")) == "Type3" && doc.Resolve(dict.GetValueOrDefault("FontMatrix")) is List<object?> m && m.Count > 0)
            _scale = doc.Num(m[0]);
        var first = (int)doc.Num(dict.GetValueOrDefault("FirstChar"));
        if (doc.Resolve(dict.GetValueOrDefault("Widths")) is List<object?> widths)
            for (var i = 0; i < widths.Count; i++) _widths[first + i] = doc.Num(widths[i]);
        var descriptor = doc.Dict(dict.GetValueOrDefault("FontDescriptor"));
        _defaultWidth = descriptor?.GetValueOrDefault("MissingWidth") is { } mw ? doc.Num(mw) : 500;

        // A simple font without ToUnicode: its encoding, the Differences over it, else WinAnsi (what most producers write).
        var encoding = doc.Resolve(dict.GetValueOrDefault("Encoding"));
        var baseName = PdfDocument.Name(encoding) ?? PdfDocument.Name(doc.Dict(encoding)?.GetValueOrDefault("BaseEncoding")) ?? "WinAnsiEncoding";
        for (var code = 32; code < 256; code++)
            if (!_unicode.ContainsKey(code) && BaseChar(baseName, code) is { } c) _unicode[code] = c;
        if (doc.Resolve(doc.Dict(encoding)?.GetValueOrDefault("Differences")) is List<object?> differences)
        {
            var code = 0;
            foreach (var item in differences)
            {
                if (doc.Resolve(item) is double d) code = (int)d;
                else if (item is PdfName glyph)
                {
                    if (GlyphNames.ToUnicode(glyph.Value) is { } u && !HasMapFor(code)) _unicode[code] = u;
                    code++;
                }
            }
        }
        // ⚠ A simple font's codes are one byte, whatever its ToUnicode map declares (Word writes <0000> <FFFF> there).
        _codespace.Add((1, 0, 0xFF));
        HasText = true;
    }

    private readonly List<(int Length, int Low, int High)> _cmapCodespace = [];

    private bool HasMapFor(int code) => _fromToUnicode.Contains(code);
    private readonly HashSet<int> _fromToUnicode = [];

    /// <summary>The codes of <paramref name="bytes"/>, each with its length in bytes.</summary>
    public IEnumerable<int> Codes(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length;)
        {
            var taken = 0;
            var code = 0;
            foreach (var (length, low, high) in _codespace)
            {
                if (i + length > bytes.Length) continue;
                var c = 0;
                for (var k = 0; k < length; k++) c = (c << 8) | bytes[i + k];
                if (c >= low && c <= high) { taken = length; code = c; break; }
            }
            if (taken == 0) { taken = Composite && i + 1 < bytes.Length ? 2 : 1; code = taken == 2 ? (bytes[i] << 8) | bytes[i + 1] : bytes[i]; }
            i += taken;
            yield return code;
        }
    }

    /// <summary>The characters of <paramref name="code"/>; null when the font does not say.</summary>
    public string? Text(int code) => _unicode.TryGetValue(code, out var s) ? s : null;

    /// <summary>The advance of <paramref name="code"/> in text space, for a font size of 1.</summary>
    public double Width(int code) => (_widths.TryGetValue(code, out var w) ? w : _defaultWidth) * _scale;

    /// <summary>Whether <paramref name="code"/> is a one-byte space (word spacing applies to it alone).</summary>
    public bool IsSingleByteSpace(int code) => !Composite && code == 32;

    private void ReadCidWidths(PdfDocument doc, List<object?> w)
    {
        for (var i = 0; i < w.Count;)
        {
            var first = (int)doc.Num(w[i]);
            if (i + 1 < w.Count && doc.Resolve(w[i + 1]) is List<object?> run)
            {
                for (var k = 0; k < run.Count; k++) { Tick(doc.Ct); _widths[first + k] = doc.Num(run[k]); }
                i += 2;
            }
            else if (i + 2 < w.Count)
            {
                var last = (int)doc.Num(w[i + 1]);
                var width = doc.Num(w[i + 2]);
                for (var c = first; c <= last && c - first < 65536; c++) { Tick(doc.Ct); _widths[c] = width; }
                i += 3;
            }
            else break;
        }
    }

    private void ReadCMap(string cmap, CancellationToken ct)
    {
        var lexer = new PdfLexer(Encoding.Latin1.GetBytes(cmap));
        var stack = new List<object?>();
        while (!lexer.AtEnd)
        {
            Tick(ct);
            var token = lexer.Read(refs: false);
            if (token is not PdfOperator op) { stack.Add(token); continue; }
            switch (op.Value)
            {
                case "endcodespacerange":
                    for (var i = 0; i + 1 < stack.Count; i += 2)
                        if (stack[i] is PdfString lo && stack[i + 1] is PdfString hi)
                            _cmapCodespace.Add((lo.Bytes.Length, Int(lo.Bytes), Int(hi.Bytes)));
                    break;
                case "endbfchar":
                    for (var i = 0; i + 1 < stack.Count; i += 2)
                        if (stack[i] is PdfString src && stack[i + 1] is PdfString dst) Map(Int(src.Bytes), Utf16(dst.Bytes));
                    break;
                case "endbfrange":
                    for (var i = 0; i + 2 < stack.Count; i += 3)
                    {
                        if (stack[i] is not PdfString lo || stack[i + 1] is not PdfString hi) continue;
                        int from = Int(lo.Bytes), to = Int(hi.Bytes);
                        if (to - from > 65535 || to < from) continue;
                        if (stack[i + 2] is PdfString start)
                        {
                            var bytes = (byte[])start.Bytes.Clone();
                            for (var c = from; c <= to; c++)
                            {
                                Tick(ct);
                                Map(c, Utf16(bytes));
                                if (bytes.Length > 0) bytes[^1]++;
                            }
                        }
                        else if (stack[i + 2] is List<object?> each)
                            for (var c = from; c <= to && c - from < each.Count; c++)
                                if (each[c - from] is PdfString s) { Tick(ct); Map(c, Utf16(s.Bytes)); }
                    }
                    break;
            }
            if (op.Value.StartsWith("begin", StringComparison.Ordinal) || op.Value.StartsWith("end", StringComparison.Ordinal)) stack.Clear();
        }
    }

    /// <summary>The entries one font may write — a /W array or a ToUnicode map of a few megabytes can name ranges of
    /// 65 536 codes a hundred thousand times —, the read's deadline checked as it goes.</summary>
    private const int MaxFontWork = 2_000_000;
    private int _work;

    private void Tick(CancellationToken ct)
    {
        if (++_work > MaxFontWork) throw new DocumentLimitException("A PDF font names more codes than a font holds");
        if ((_work & 4095) == 0) ct.ThrowIfCancellationRequested();
    }

    private void Map(int code, string text)
    {
        _unicode[code] = text;
        _fromToUnicode.Add(code);
    }

    private static int Int(byte[] bytes)
    {
        var v = 0;
        foreach (var b in bytes.Take(4)) v = (v << 8) | b;
        return v;
    }

    private static string Utf16(byte[] bytes) =>
        bytes.Length % 2 == 0 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);

    private static string? BaseChar(string encoding, int code)
    {
        if (encoding == "MacRomanEncoding" && code >= 128) return MacRoman[code - 128].ToString();
        if (code is >= 128 and < 160 && encoding != "MacRomanEncoding") return WinAnsiHigh[code - 128] is var c && c != '\0' ? c.ToString() : null;
        if (code == 127) return null;
        return ((char)code).ToString();   // WinAnsi and Standard agree with Latin-1 here, close enough for words
    }

    private static readonly string WinAnsiHigh =
        "€\0‚ƒ„…†‡ˆ‰Š‹Œ\0Ž\0\0‘’“”•–—˜™š›œ\0žŸ";

    private static readonly string MacRoman =
        "ÄÅÇÉÑÖÜáàâäãåçéèêëíìîïñóòôöõúùûü†°¢£§•¶ß®©™´¨≠ÆØ∞±≤≥¥µ∂∑∏π∫ªºΩæø¿¡¬√ƒ≈∆«»… ÀÃÕŒœ–—“”‘’÷◊ÿŸ⁄€‹›ﬁﬂ‡·‚„‰ÂÊÁËÈÍÎÏÌÓÔÒÚÛÙıˆ˜¯˘˙˚¸˝˛ˇ";
}

/// <summary>The characters of the glyph names a simple font's Differences use: the common Latin ones, <c>uniXXXX</c> and
/// <c>uXXXX</c>. A name not known gives nothing — a missing letter, never a wrong one.</summary>
internal static class GlyphNames
{
    private static readonly Dictionary<string, string> Known = Build();

    public static string? ToUnicode(string name)
    {
        var dot = name.IndexOf('.');
        if (dot > 0) name = name[..dot];   // "a.sc", "one.oldstyle"
        if (Known.TryGetValue(name, out var s)) return s;
        if (name.Length == 1 && char.IsLetter(name[0])) return name;
        if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length >= 7
            && int.TryParse(name.AsSpan(3, 4), System.Globalization.NumberStyles.HexNumber, null, out var u)) return ((char)u).ToString();
        if (name.StartsWith('u') && name.Length is 5 or 6 or 7
            && int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v) && v < 0x110000
            && v is < 0xD800 or > 0xDFFF)   // a surrogate is no character: ConvertFromUtf32 throws, and the whole file failed
            return char.ConvertFromUtf32(v);
        return null;
    }

    private static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["space"] = " ", ["exclam"] = "!", ["quotedbl"] = "\"", ["numbersign"] = "#", ["dollar"] = "$", ["percent"] = "%",
            ["ampersand"] = "&", ["quotesingle"] = "'", ["quoteright"] = "’", ["quoteleft"] = "‘", ["parenleft"] = "(",
            ["parenright"] = ")", ["asterisk"] = "*", ["plus"] = "+", ["comma"] = ",", ["hyphen"] = "-", ["minus"] = "−",
            ["period"] = ".", ["slash"] = "/", ["colon"] = ":", ["semicolon"] = ";", ["less"] = "<", ["equal"] = "=",
            ["greater"] = ">", ["question"] = "?", ["at"] = "@", ["bracketleft"] = "[", ["backslash"] = "\\",
            ["bracketright"] = "]", ["asciicircum"] = "^", ["underscore"] = "_", ["grave"] = "`", ["braceleft"] = "{",
            ["bar"] = "|", ["braceright"] = "}", ["asciitilde"] = "~", ["quotedblleft"] = "“", ["quotedblright"] = "”",
            ["quotesinglbase"] = "‚", ["quotedblbase"] = "„", ["endash"] = "–", ["emdash"] = "—", ["bullet"] = "•",
            ["ellipsis"] = "…", ["dagger"] = "†", ["daggerdbl"] = "‡", ["guillemotleft"] = "«", ["guillemotright"] = "»",
            ["guilsinglleft"] = "‹", ["guilsinglright"] = "›", ["fi"] = "fi", ["fl"] = "fl", ["ff"] = "ff", ["ffi"] = "ffi",
            ["ffl"] = "ffl", ["germandbls"] = "ß", ["degree"] = "°", ["section"] = "§", ["paragraph"] = "¶",
            ["copyright"] = "©", ["registered"] = "®", ["trademark"] = "™", ["Euro"] = "€", ["sterling"] = "£", ["yen"] = "¥",
            ["cent"] = "¢", ["multiply"] = "×", ["divide"] = "÷", ["plusminus"] = "±", ["dotlessi"] = "ı", ["OE"] = "Œ",
            ["oe"] = "œ", ["AE"] = "Æ", ["ae"] = "æ", ["Oslash"] = "Ø", ["oslash"] = "ø", ["Lslash"] = "Ł", ["lslash"] = "ł",
            ["nbspace"] = " ", ["periodcentered"] = "·", ["exclamdown"] = "¡", ["questiondown"] = "¿",
        };
        string[] digits = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
        for (var i = 0; i < 10; i++) d[digits[i]] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Accented Latin letters: the base letter's name plus the accent's ("eacute", "Ccedilla").
        (string Accent, string Combining)[] accents =
            [("acute", "́"), ("grave", "̀"), ("circumflex", "̂"), ("dieresis", "̈"), ("tilde", "̃"),
             ("ring", "̊"), ("cedilla", "̧"), ("caron", "̌"), ("macron", "̄"), ("breve", "̆"),
             ("ogonek", "̨"), ("dotaccent", "̇"), ("hungarumlaut", "̋")];
        foreach (var letter in "aeiouycnszgrldtAEIOUYCNSZGRLDT")
            foreach (var (accent, combining) in accents)
            {
                var composed = (letter + combining).Normalize(NormalizationForm.FormC);
                if (composed.Length == 1) d[letter + accent] = composed;
            }
        return d;
    }
}
