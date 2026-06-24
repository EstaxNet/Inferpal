using System.Text;

namespace Inferpal.Services.CodeActions;

/// <summary>
/// Pure, testable re-indentation of inline-edit ("Edit with AI") model output.
/// <para>
/// Local models frequently drop the leading indentation of the lines they
/// <b>insert</b> (flush to column 0) while keeping the surrounding lines correct.
/// A single uniform shift — the previous approach — cannot repair this because the
/// breakage is non-uniform: the flush-left lines poison any "minimum indent"
/// detection, so the whole block ends up mis-shifted.
/// </para>
/// <para>
/// This re-anchors the block so its first non-empty line matches the original's
/// base indentation, then raises any line that sits <b>below</b> its structural
/// (brace-depth) floor up to that floor. Lines the model already indented deeper
/// than the brace floor (e.g. a brace-less <c>if</c> body, a <c>switch</c> case
/// body, a continuation line) keep their indentation — the reindenter never
/// flattens correct nesting. Brackets inside strings, char literals and comments
/// are ignored, including multi-line verbatim strings and block comments, whose
/// continuation lines are preserved verbatim.
/// </para>
/// <para>
/// For brace-less code (e.g. Python) there is no structural floor to fall back on,
/// so only the re-anchoring shift is applied.
/// </para>
/// </summary>
internal static class InlineEditReindenter
{
    private enum ScanState { Normal, BlockComment, VerbatimString }

    /// <summary>
    /// Re-indents <paramref name="edited"/> to match the base indentation and style
    /// of <paramref name="original"/>, repairing dropped indentation on inserted lines.
    /// </summary>
    public static string Reindent(string original, string edited)
    {
        if (string.IsNullOrWhiteSpace(edited)) return edited;

        var baseIndent = GetLeadingWhitespace(original);
        var (unit, unitWidth, useTabs) = DetectIndent(original, baseIndent);
        var braceBased = edited.IndexOf('{') >= 0 || edited.IndexOf('}') >= 0;

        var editFirst      = GetLeadingWhitespace(edited);
        var editFirstWidth = VisualWidth(editFirst, useTabs);

        var lines = edited.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb    = new StringBuilder(edited.Length + lines.Length * baseIndent.Length);
        var state = ScanState.Normal;
        var depth = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            var raw        = lines[i];
            var startState = state;

            // Continuation of a multi-line verbatim string / block comment: the leading
            // whitespace may be part of the literal — preserve the line byte-for-byte.
            if (startState != ScanState.Normal)
            {
                sb.Append(raw);
                ScanLine(raw, ref state); // advance state (no brackets counted while inside)
                continue;
            }

            var ws      = LeadingWhitespace(raw);
            var trimmed = raw[ws.Length..];

            // Blank / whitespace-only line: emit empty to avoid trailing spaces.
            if (trimmed.Length == 0) continue;

            var (minPrefix, net) = ScanLine(trimmed, ref state);

            // Structural floor: how deep this line sits given the running brace depth,
            // accounting for any leading closers (minPrefix <= 0).
            var floorUnits = Math.Max(0, depth + minPrefix);

            // Model's own relative indent, re-anchored to the original's first line.
            // The first non-empty line always anchors to the base (relative 0).
            var modelUnits = 0;
            if (i > 0)
            {
                var relWidth = VisualWidth(ws, useTabs) - editFirstWidth;
                modelUnits = relWidth <= 0
                    ? 0
                    : (int)Math.Round(relWidth / (double)unitWidth, MidpointRounding.AwayFromZero);
            }

            // Brace code: never go below the structural floor, but keep deeper model
            // indentation (brace-less bodies, switch cases, continuations).
            // Brace-less code: trust the re-anchored model indentation only.
            var units = braceBased ? Math.Max(modelUnits, floorUnits) : modelUnits;

            sb.Append(baseIndent);
            for (var u = 0; u < units; u++) sb.Append(unit);
            sb.Append(trimmed);

            depth = Math.Max(0, depth + net);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Scans a single line, returning the minimum running bracket balance over its prefix
    /// (<c>minPrefix</c>, &lt;= 0, = leading closers) and the net balance change.
    /// Brackets inside strings, char literals and comments are ignored;
    /// <paramref name="state"/> carries multi-line verbatim-string / block-comment context.
    /// </summary>
    private static (int minPrefix, int net) ScanLine(string line, ref ScanState state)
    {
        int balance = 0, minPrefix = 0, i = 0, n = line.Length;

        while (i < n)
        {
            var c = line[i];

            if (state == ScanState.BlockComment)
            {
                if (c == '*' && i + 1 < n && line[i + 1] == '/') { state = ScanState.Normal; i += 2; }
                else i++;
                continue;
            }

            if (state == ScanState.VerbatimString)
            {
                if (c == '"')
                {
                    if (i + 1 < n && line[i + 1] == '"') i += 2;      // "" escape
                    else { state = ScanState.Normal; i++; }
                }
                else i++;
                continue;
            }

            // ── Normal ──────────────────────────────────────────────────────────
            if (c == '/' && i + 1 < n && line[i + 1] == '/') break;                       // line comment
            if (c == '/' && i + 1 < n && line[i + 1] == '*') { state = ScanState.BlockComment; i += 2; continue; }

            if (c == '\'')                                                                // char literal
            {
                i++;
                while (i < n)
                {
                    if (line[i] == '\\') { i += 2; continue; }
                    if (line[i] == '\'') { i++; break; }
                    i++;
                }
                continue;
            }

            if (c == '"' || c == '$' || c == '@')                                         // string literal (incl. $, @, $@)
            {
                var j  = i;
                var at = false;
                while (j < n && (line[j] == '$' || line[j] == '@'))
                {
                    if (line[j] == '@') at = true;
                    j++;
                }

                if (j < n && line[j] == '"')
                {
                    i = j + 1;
                    if (at)                                                              // verbatim: may span lines
                    {
                        var closed = false;
                        while (i < n)
                        {
                            if (line[i] == '"')
                            {
                                if (i + 1 < n && line[i + 1] == '"') { i += 2; continue; }
                                i++; closed = true; break;
                            }
                            i++;
                        }
                        if (!closed) state = ScanState.VerbatimString;
                        continue;
                    }

                    while (i < n)                                                         // regular (maybe interpolated)
                    {
                        if (line[i] == '\\') { i += 2; continue; }
                        if (line[i] == '"') { i++; break; }
                        i++;
                    }
                    continue;
                }

                i++;                                                                     // lone $/@ — not a string start
                continue;
            }

            if (c is '{' or '(' or '[') { balance++; }
            else if (c is '}' or ')' or ']') { balance--; if (balance < minPrefix) minPrefix = balance; }
            i++;
        }

        return (minPrefix, balance);
    }

    /// <summary>Detects the indentation unit (tab or N spaces) and its visual width from the original.</summary>
    private static (string unit, int unitWidth, bool useTabs) DetectIndent(string original, string baseIndent)
    {
        var split   = original.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var useTabs = baseIndent.IndexOf('\t') >= 0;

        var widths = new SortedSet<int>();
        foreach (var line in split)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var ws = LeadingWhitespace(line);
            if (ws.IndexOf('\t') >= 0) useTabs = true;
            widths.Add(ws.Length);
        }

        if (useTabs) return ("\t", 1, true);

        // Spaces: smallest positive step between distinct indent widths (default 4).
        int step = 0, prev = -1;
        foreach (var w in widths)
        {
            if (prev >= 0)
            {
                var d = w - prev;
                if (d > 0 && (step == 0 || d < step)) step = d;
            }
            prev = w;
        }
        if (step <= 0) step = 4;
        return (new string(' ', step), step, false);
    }

    /// <summary>Visual width of a leading-whitespace run: space = 1 column; tab = 1 unit in tab mode.</summary>
    private static int VisualWidth(string ws, bool useTabs)
    {
        if (!useTabs) return ws.Length;
        var w = 0;
        foreach (var c in ws) if (c == '\t') w++;
        return w;
    }

    /// <summary>Leading whitespace (spaces/tabs) of a single line.</summary>
    private static string LeadingWhitespace(string line)
    {
        var trimmed = line.TrimStart(' ', '\t');
        return line[..(line.Length - trimmed.Length)];
    }

    /// <summary>Leading whitespace of the first non-empty line of <paramref name="code"/>.</summary>
    private static string GetLeadingWhitespace(string code)
    {
        foreach (var line in code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            return LeadingWhitespace(line);
        }
        return string.Empty;
    }
}
