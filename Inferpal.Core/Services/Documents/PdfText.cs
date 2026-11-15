using System.Text;

namespace Inferpal.Services.Documents;

/// <summary>
/// The text of a PDF: each page's content streams run through their text operators. Spaces and line breaks come from
/// where the pen moves — a PDF rarely writes a space between words — measured against the fonts' widths, and every jump
/// the reader cannot explain separates words rather than gluing them.
/// </summary>
internal static class PdfText
{
    /// <summary>Pages read at most; past it, reading stops and says so.</summary>
    internal const int MaxPages = 5_000;
    /// <summary>Characters kept at most — far past what one attachment sends; past it, reading stops and says so.</summary>
    internal const int MaxChars = 2_000_000;
    /// <summary>Content operators run in one document, forms drawn inside forms included: a form drawn a thousand times by
    /// itself, eight levels deep, would otherwise run forever.</summary>
    private const int MaxOperations = 20_000_000;
    private const int MaxFormDepth = 8;

    internal static DocumentExtraction Extract(byte[] bytes, CancellationToken ct, string? password = null)
    {
        if (bytes.Length < 8 || bytes.AsSpan(0, Math.Min(1024, bytes.Length)).IndexOf("%PDF"u8) < 0)
            return new(string.Empty, 0, 0, DocumentProblem.Unreadable);

        var doc = new PdfDocument(bytes, ct, password);
        if (doc.UnsupportedEncryption) return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
        if (doc.Locked) return new(string.Empty, 0, 0, DocumentProblem.Encrypted);
        var root = doc.Dict(doc.Trailer.GetValueOrDefault("Root"));
        if (root is null) return new(string.Empty, 0, 0, DocumentProblem.Unreadable);

        var pages = new List<(Dictionary<string, object?> Page, Dictionary<string, object?>? Resources)>();
        CollectPages(doc, root.GetValueOrDefault("Pages"), null, pages, [], 0, ct);
        if (pages.Count == 0) return new(string.Empty, 0, 0, DocumentProblem.Unreadable);

        var text = new StringBuilder();
        var empty = 0;
        var read = 0;
        var stoppedAt = pages.Count > MaxPages ? MaxPages : 0;   // one page past the cap is collected, to know there are more
        if (stoppedAt > 0) pages.RemoveRange(MaxPages, pages.Count - MaxPages);
        var fonts = new Dictionary<object, PdfFont>(ReferenceEqualityComparer.Instance);
        var budget = new Budget();
        foreach (var (page, resources) in pages)
        {
            ct.ThrowIfCancellationRequested();
            var reader = new PageReader(doc, fonts, budget, ct);
            foreach (var content in Contents(doc, page.GetValueOrDefault("Contents"))) reader.Run(content, resources, 0, []);
            read++;
            var pageText = reader.Text.Trim();
            if (pageText.Length == 0) { empty++; continue; }
            if (text.Length > 0) text.Append("\n\n");
            text.Append(pageText);
            if (text.Length >= MaxChars)
            {
                text.Length = MaxChars;
                stoppedAt = read;
                break;
            }
        }
        var result = text.ToString();
        return new(result, pages.Count, result.Length == 0 ? 0 : empty, result.Length == 0 ? DocumentProblem.NoText : DocumentProblem.None,
                   stoppedAt);
    }

    /// <summary>What one document's pages may spend together.</summary>
    private sealed class Budget
    {
        public long Operations;
    }

    private static void CollectPages(PdfDocument doc, object? node, Dictionary<string, object?>? inherited,
        List<(Dictionary<string, object?>, Dictionary<string, object?>?)> pages, HashSet<object> seen, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dict = doc.Dict(node);
        if (dict is null || depth > 64 || pages.Count > MaxPages || !seen.Add(dict)) return;
        var resources = doc.Dict(dict.GetValueOrDefault("Resources")) ?? inherited;
        if (doc.Resolve(dict.GetValueOrDefault("Kids")) is List<object?> kids)
        {
            foreach (var kid in kids) CollectPages(doc, kid, resources, pages, seen, depth + 1, ct);
            return;
        }
        pages.Add((dict, resources));
    }

    private static IEnumerable<byte[]> Contents(PdfDocument doc, object? contents)
    {
        switch (doc.Resolve(contents))
        {
            case PdfStream s: yield return doc.Decode(s); break;
            case List<object?> list:
                // One stream split in pieces: the pieces join (a token may straddle two), with a separator between them.
                var joined = new List<byte>();
                foreach (var item in list)
                    if (doc.Resolve(item) is PdfStream part) { joined.AddRange(doc.Decode(part)); joined.Add(10); }
                yield return [.. joined];
                break;
        }
    }

    /// <summary>Runs the content streams of one page and keeps the text they show.</summary>
    private sealed class PageReader(PdfDocument doc, Dictionary<object, PdfFont> fonts, Budget budget, CancellationToken ct)
    {
        private readonly StringBuilder _text = new();
        private double[] _tm = [1, 0, 0, 1, 0, 0];
        private double[] _lm = [1, 0, 0, 1, 0, 0];
        private PdfFont? _font;
        private double _size = 1, _charSpacing, _wordSpacing, _scale = 1, _leading;
        private double _penX = double.NaN, _penY = double.NaN;   // where the last glyph ended, in text space

        public string Text => _text.ToString();

        /// <param name="drawing">The forms being drawn around this content: a form that draws itself is not drawn again.</param>
        public void Run(byte[] content, Dictionary<string, object?>? resources, int depth, HashSet<PdfStream> drawing)
        {
            var lexer = new PdfLexer(content);
            var operands = new List<object?>();
            while (!lexer.AtEnd)
            {
                // One count for the whole document, forms included: a short stream drawn a million times is still counted.
                if (++budget.Operations > MaxOperations) throw new DocumentLimitException("A PDF runs more operators than a document holds");
                if ((budget.Operations & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (_text.Length > MaxChars) return;
                var before = lexer.Position;
                var token = lexer.Read(refs: false);
                if (lexer.Position == before) { lexer.Position++; continue; }
                if (token is not PdfOperator op) { operands.Add(token); if (operands.Count > 64) operands.RemoveAt(0); continue; }
                Apply(op.Value, operands, resources, depth, lexer, content, drawing);
                operands.Clear();
            }
        }

        private double N(List<object?> o, int i) => i < o.Count && o[i] is double d ? d : 0;

        private void Apply(string op, List<object?> o, Dictionary<string, object?>? resources, int depth, PdfLexer lexer, byte[] content,
                           HashSet<PdfStream> drawing)
        {
            switch (op)
            {
                case "BT": _tm = [1, 0, 0, 1, 0, 0]; _lm = [1, 0, 0, 1, 0, 0]; break;
                case "ET": break;
                case "Tf":
                    _size = N(o, 1) is 0 ? 1 : N(o, 1);
                    _font = o.Count > 0 && o[0] is PdfName fn ? Font(resources, fn.Value) : null;
                    break;
                case "Tc": _charSpacing = N(o, 0); break;
                case "Tw": _wordSpacing = N(o, 0); break;
                case "Tz": _scale = N(o, 0) / 100; break;
                case "TL": _leading = N(o, 0); break;
                case "Td": Move(N(o, 0), N(o, 1)); break;
                case "TD": _leading = -N(o, 1); Move(N(o, 0), N(o, 1)); break;
                case "T*": Move(0, -_leading); break;
                case "Tm":
                    if (o.Count >= 6) { _tm = [N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 4), N(o, 5)]; _lm = (double[])_tm.Clone(); }
                    break;
                case "Tj": if (o.Count > 0 && o[^1] is PdfString s) Show(s.Bytes); break;
                case "'": Move(0, -_leading); if (o.Count > 0 && o[^1] is PdfString s1) Show(s1.Bytes); break;
                case "\"":
                    _wordSpacing = N(o, 0); _charSpacing = N(o, 1); Move(0, -_leading);
                    if (o.Count > 2 && o[2] is PdfString s2) Show(s2.Bytes);
                    break;
                case "TJ":
                    if (o.Count > 0 && o[^1] is List<object?> items)
                        foreach (var item in items)
                        {
                            if (item is PdfString part) Show(part.Bytes);
                            else if (item is double adjust)
                            {
                                // A kerning that large is a word space drawn by moving the pen.
                                if (-adjust > 180) Separate(' ');
                                Advance(-adjust / 1000 * _size * _scale);
                            }
                        }
                    break;
                case "Do" when depth < MaxFormDepth && o.Count > 0 && o[0] is PdfName xo:
                    var xobjects = doc.Dict(resources?.GetValueOrDefault("XObject"));
                    if (doc.Resolve(xobjects?.GetValueOrDefault(xo.Value)) is PdfStream form && PdfDocument.Name(form.Dict.GetValueOrDefault("Subtype")) == "Form"
                        && drawing.Add(form))
                    {
                        var saveTm = _tm; var saveLm = _lm;
                        Separate('\n');
                        Run(doc.Decode(form), doc.Dict(form.Dict.GetValueOrDefault("Resources")) ?? resources, depth + 1, drawing);
                        drawing.Remove(form);
                        _tm = saveTm; _lm = saveLm;
                        _penX = double.NaN;
                    }
                    break;
                case "ID":
                    // An inline image's bytes run up to "EI": skipped whole, never read as operators.
                    var end = lexer.Position;
                    while (end + 2 < content.Length && !(content[end] is 32 or 10 or 13 && content[end + 1] == (byte)'E' && content[end + 2] == (byte)'I'
                                                          && (end + 3 >= content.Length || content[end + 3] is 32 or 10 or 13)))
                        end++;
                    lexer.Position = Math.Min(content.Length, end + 3);
                    break;
            }
        }

        private PdfFont? Font(Dictionary<string, object?>? resources, string name)
        {
            var dictValue = doc.Dict(resources?.GetValueOrDefault("Font"))?.GetValueOrDefault(name);
            var dict = doc.Dict(dictValue);
            if (dict is null) return null;
            if (!fonts.TryGetValue(dict, out var font)) fonts[dict] = font = new PdfFont(doc, dict);
            return font;
        }

        /// <summary>Td: the line matrix moves, and the text matrix with it.</summary>
        private void Move(double tx, double ty)
        {
            _lm = [_lm[0], _lm[1], _lm[2], _lm[3], tx * _lm[0] + ty * _lm[2] + _lm[4], tx * _lm[1] + ty * _lm[3] + _lm[5]];
            _tm = (double[])_lm.Clone();
        }

        private void Advance(double tx)
        {
            _tm[4] += tx * _tm[0];
            _tm[5] += tx * _tm[1];
        }

        private void Show(byte[] bytes)
        {
            if (_font is null) return;
            // Where this run starts against where the last one ended: another line, a gap (a space), or glued.
            var height = Math.Abs(_size * Math.Sqrt(_tm[2] * _tm[2] + _tm[3] * _tm[3]));
            var width = Math.Abs(_size * Math.Sqrt(_tm[0] * _tm[0] + _tm[1] * _tm[1]));
            if (!double.IsNaN(_penX))
            {
                var dy = Math.Abs(_tm[5] - _penY);
                var dx = _tm[4] - _penX;
                if (dy > Math.Max(height, 0.01) * 0.5) Separate('\n');
                else if (dx > width * 0.12 || dx < -width * 0.5) Separate(' ');
            }
            foreach (var code in _font.Codes(bytes))
            {
                if (_font.Text(code) is { } s)
                {
                    foreach (var ch in s)
                        if (!char.IsControl(ch) || ch is '\t') _text.Append(ch is '\t' or ' ' ? ' ' : ch);
                }
                var advance = (_font.Width(code) * _size + _charSpacing + (_font.IsSingleByteSpace(code) ? _wordSpacing : 0)) * _scale;
                Advance(advance);
            }
            _penX = _tm[4];
            _penY = _tm[5];
        }

        private void Separate(char separator)
        {
            if (_text.Length == 0) return;
            var last = _text[^1];
            if (last == '\n' || (last == ' ' && separator == ' ')) return;
            if (last == ' ' && separator == '\n') _text.Length--;
            _text.Append(separator);
        }
    }
}
