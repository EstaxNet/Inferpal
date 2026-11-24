using System.Globalization;
using System.Text;

namespace Inferpal.Services.Documents;

/// <summary>
/// The text of an RTF document: its words, paragraphs and table rows, without what RTF keeps beside them (font and colour
/// tables, styles, document properties, pictures, field codes). A byte escape is decoded in the code page of the font in
/// use — Windows' own rich edit control writes Chinese in GB2312 under a font that declares it, not in Unicode.
/// </summary>
/// <remarks>No RTF library fits: RtfPipe, the common one, is partly under the CPOL, which the GPL does not allow.</remarks>
internal static class RtfText
{
    /// <summary>Groups nested deeper than this are not read: a few thousand levels say a file built to exhaust a reader.</summary>
    private const int MaxDepth = 512;

    /// <summary>Destinations whose text is not the document's.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "themedata", "colorschememapping", "datastore",
        "latentstyles", "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "mmathPr", "fldinst",
        "filetbl", "revtbl", "listtext", "pntext", "pntxta", "pntxtb", "bkmkstart", "bkmkend", "nonshppict", "shppict",
    };

    private sealed class State
    {
        public bool Skip;
        public int UnicodeSkip = 1;
        public int CodePage;
        public State Copy() => (State)MemberwiseClone();
    }

    internal static DocumentExtraction Extract(byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "{\\rtf") return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
        var fontCodePages = new Dictionary<int, int>();
        var defaultCodePage = 1252;
        var text = new StringBuilder();
        var pending = new List<byte>();   // bytes of \'hh escapes, decoded together (a double-byte character is two escapes)
        var stack = new Stack<State>();
        var state = new State { CodePage = defaultCodePage };
        var inFontTable = 0;
        var currentFont = -1;
        var skipChars = 0;   // characters left to skip after a \u escape
        var stoppedAt = 0;

        void Flush()
        {
            if (pending.Count == 0) return;
            text.Append(DocumentText.InCodePage(pending.ToArray(), state.CodePage == 0 ? defaultCodePage : state.CodePage));
            pending.Clear();
        }

        var i = 0;
        while (i < bytes.Length)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (text.Length > PdfText.MaxChars) { stoppedAt = 1; break; }
            var c = (char)bytes[i];
            if (c == '{')
            {
                Flush();
                if (stack.Count >= MaxDepth) throw new DocumentLimitException("An RTF document nested past its depth limit");
                stack.Push(state);
                state = state.Copy();
                if (inFontTable > 0) inFontTable++;
                i++;
                continue;
            }
            if (c == '}')
            {
                Flush();
                if (inFontTable > 0) inFontTable--;
                state = stack.Count > 0 ? stack.Pop() : state;
                i++;
                continue;
            }
            if (c == '\\' && i + 1 < bytes.Length)
            {
                var next = (char)bytes[i + 1];
                if (next == '\'' && i + 3 < bytes.Length)
                {
                    if (byte.TryParse(Encoding.ASCII.GetString(bytes, i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    {
                        if (skipChars > 0) skipChars--;
                        else if (!state.Skip) pending.Add(b);
                    }
                    i += 4;
                    continue;
                }
                if (!char.IsAsciiLetter(next))
                {
                    Flush();
                    // A control symbol: an escaped brace or backslash is text; \* marks an optional destination.
                    if (next is '\\' or '{' or '}') { if (!state.Skip) text.Append(next); }
                    else if (next == '~') { if (!state.Skip) text.Append(' '); }
                    else if (next == '*') state.Skip = true;
                    else if (next is '\n' or '\r') { if (!state.Skip) text.Append('\n'); }
                    i += 2;
                    continue;
                }
                // A control word: letters, an optional signed number, an optional space that belongs to it.
                var start = i + 1;
                var end = start;
                while (end < bytes.Length && char.IsAsciiLetter((char)bytes[end])) end++;
                var word = Encoding.ASCII.GetString(bytes, start, end - start);
                var numberStart = end;
                if (end < bytes.Length && bytes[end] == '-') end++;
                while (end < bytes.Length && char.IsAsciiDigit((char)bytes[end])) end++;
                int? number = end > numberStart && int.TryParse(Encoding.ASCII.GetString(bytes, numberStart, end - numberStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
                if (end < bytes.Length && bytes[end] == ' ') end++;
                i = end;
                Flush();
                if (Skipped.Contains(word))
                {
                    if (word == "fonttbl") inFontTable = 1;
                    state.Skip = true;
                    if (word != "fonttbl") continue;
                }
                switch (word)
                {
                    case "ansicpg" when number is > 0: defaultCodePage = number.Value; if (state.CodePage == 1252) state.CodePage = number.Value; break;
                    case "f" when number is { } font:
                        if (inFontTable > 0) currentFont = font;
                        else if (fontCodePages.TryGetValue(font, out var page)) state.CodePage = page;
                        else state.CodePage = defaultCodePage;
                        break;
                    case "fcharset" when number is { } charset && inFontTable > 0 && currentFont >= 0:
                        if (CharsetCodePage(charset) is { } cp) fontCodePages[currentFont] = cp;
                        break;
                    case "uc" when number is { } uc: state.UnicodeSkip = Math.Max(0, uc); break;
                    case "u" when number is { } u:
                        if (!state.Skip) text.Append((char)(u < 0 ? u + 65536 : u));
                        skipChars = state.UnicodeSkip;
                        break;
                    case "par" or "line" or "sect" or "page" or "row": if (!state.Skip) text.Append('\n'); break;
                    case "tab" or "cell": if (!state.Skip) text.Append('\t'); break;
                    case "emdash": if (!state.Skip) text.Append('—'); break;
                    case "endash": if (!state.Skip) text.Append('–'); break;
                    case "bullet": if (!state.Skip) text.Append('•'); break;
                    case "lquote": if (!state.Skip) text.Append('‘'); break;
                    case "rquote": if (!state.Skip) text.Append('’'); break;
                    case "ldblquote": if (!state.Skip) text.Append('“'); break;
                    case "rdblquote": if (!state.Skip) text.Append('”'); break;
                }
                continue;
            }
            if (c is '\r' or '\n') { i++; continue; }
            // Plain text: bytes in the code page in use (an RTF writer escapes what is not ASCII, but not all do).
            if (skipChars > 0) { skipChars--; i++; continue; }
            if (!state.Skip) pending.Add(bytes[i]);
            i++;
        }
        Flush();
        var read = string.Join('\n', text.ToString().Split('\n').Select(l => l.TrimEnd('\t', ' '))).Trim();
        if (read.Length == 0) return new(string.Empty, 0, 0, DocumentProblem.NoText);
        return new(PdfText.Capped(read), 0, 0, DocumentProblem.None, stoppedAt);
    }

    /// <summary>The Windows code page of an RTF font charset; null for the symbol sets, which carry no text.</summary>
    private static int? CharsetCodePage(int charset) => charset switch
    {
        0 => 1252, 128 => 932, 129 => 949, 130 => 1361, 134 => 936, 136 => 950, 161 => 1253, 162 => 1254, 163 => 1258,
        177 => 1255, 178 => 1256, 186 => 1257, 204 => 1251, 222 => 874, 238 => 1250, 77 => 10000,
        _ => null,
    };
}
