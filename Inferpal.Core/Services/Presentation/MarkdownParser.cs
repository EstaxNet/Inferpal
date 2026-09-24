using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Inferpal.Services.Presentation;

internal static class MarkdownParser
{
    private static readonly MarkdownPipeline _pipeline =
        new MarkdownPipelineBuilder()
            .UseEmphasisExtras()
            .UseAutoLinks()
            .UsePipeTables()
            .Build();

    private const string ThinkOpen  = "<think>";
    private const string ThinkClose = "</think>";

    /// <summary>
    /// Removes the model's reasoning blocks (<c>&lt;think&gt;...&lt;/think&gt;</c>) from
    /// <paramref name="content"/> and trims the result. Returns an empty string when the input is null
    /// or whitespace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reasoning is text the model EMITS — at the head of a turn, or between the turns of an agent run
    /// streamed into one message; a reply stopped while the model was still reasoning ends inside an
    /// unclosed tag, and that tail is reasoning too. A tag inside a fenced block or a code span is
    /// something the answer SHOWS: asked how to strip Qwen3's reasoning, a model answers with the regex,
    /// and removing every tag it found deleted half that answer and left a broken fence — or, for an
    /// opening tag named in backticks, everything after it. So code is copied untouched.
    /// </para>
    /// <para>
    /// ⚠ The content of a block is skipped whole, never scanned: reasoning writes code too, and a fence
    /// it opens and never closes must not turn the answer that follows into a code block.
    /// </para>
    /// </remarks>
    public static string StripThinkTags(string? content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        if (content.IndexOf(ThinkOpen, StringComparison.OrdinalIgnoreCase) < 0) return content.Trim();

        var kept = new StringBuilder(content.Length);
        var fenceChar = '\0';
        var fenceLength = 0; // > 0 while inside a fenced block
        var i = 0;
        while (i < content.Length)
        {
            // Line structure follows what is KEPT: a block removed at the head of a line leaves that line's start.
            var atLineStart = kept.Length == 0 || kept[^1] == '\n';
            if (atLineStart && ReadFence(content, i) is { } fence
                && (fenceLength == 0 || (fence.Char == fenceChar && fence.Length >= fenceLength && fence.Bare)))
            {
                if (fenceLength == 0) { fenceChar = fence.Char; fenceLength = fence.Length; }
                else fenceLength = 0;
                i = CopyLine(content, i, kept);
                continue;
            }
            if (fenceLength > 0)
            {
                i = CopyLine(content, i, kept);
                continue;
            }

            if (content[i] == '`')
            {
                var run = RunLength(content, i, '`');
                var closer = SpanCloser(content, i + run, run);
                var end = closer < 0 ? i + run : closer + run; // an unmatched run is literal text
                kept.Append(content, i, end - i);
                i = end;
                continue;
            }

            if (content[i] == '<' && string.Compare(content, i, ThinkOpen, 0, ThinkOpen.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var close = content.IndexOf(ThinkClose, i + ThinkOpen.Length, StringComparison.OrdinalIgnoreCase);
                if (close < 0) break;
                i = close + ThinkClose.Length;
                continue;
            }

            kept.Append(content[i]);
            i++;
        }
        return kept.ToString().Trim();
    }

    // A fence line: up to three spaces, then three or more backticks or tildes. `Bare` = nothing after the
    // run, which a closing fence requires. A backtick run followed by another backtick on the same line
    // is an inline span (```x```), not a fence.
    private static (char Char, int Length, bool Bare)? ReadFence(string s, int lineStart)
    {
        var i = lineStart;
        var indent = 0;
        while (i < s.Length && s[i] == ' ' && indent < 3) { i++; indent++; }
        if (i >= s.Length || (s[i] != '`' && s[i] != '~')) return null;
        var ch = s[i];
        var length = RunLength(s, i, ch);
        if (length < 3) return null;
        var eol = s.IndexOf('\n', i + length);
        var rest = s.AsSpan(i + length, (eol < 0 ? s.Length : eol) - i - length);
        if (ch == '`' && rest.Contains('`')) return null;
        return (ch, length, rest.IsWhiteSpace());
    }

    private static int RunLength(string s, int start, char ch)
    {
        var end = start;
        while (end < s.Length && s[end] == ch) end++;
        return end - start;
    }

    // Index of the backtick run of exactly `run` characters that closes a code span, or -1. A span does not
    // cross a blank line: past a paragraph break an unmatched run is literal text.
    private static int SpanCloser(string s, int from, int run)
    {
        var i = from;
        while (i < s.Length)
        {
            if (s[i] == '`')
            {
                var length = RunLength(s, i, '`');
                if (length == run) return i;
                i += length;
                continue;
            }
            if (s[i] == '\n')
            {
                var j = i + 1;
                while (j < s.Length && (s[j] == ' ' || s[j] == '\t' || s[j] == '\r')) j++;
                if (j >= s.Length || s[j] == '\n') return -1;
            }
            i++;
        }
        return -1;
    }

    private static int CopyLine(string s, int start, StringBuilder kept)
    {
        var eol = s.IndexOf('\n', start);
        var end = eol < 0 ? s.Length : eol + 1;
        kept.Append(s, start, end - start);
        return end;
    }

    /// <summary>
    /// The text a chat message shows: an assistant turn without the model's inline reasoning, any other turn whole — a
    /// question may quote the tag. The one reader for what shows, copies, searches or exports a message: a streamed
    /// answer is kept with its reasoning, and every reader that took the raw content leaked it.
    /// </summary>
    public static string ShownText(string? role, string? content) =>
        role == "assistant" ? StripThinkTags(content) : content ?? string.Empty;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="s"/> contains at least one printable character
    /// (letter, digit, punctuation, or symbol).
    /// <para>
    /// Unlike <see cref="string.IsNullOrWhiteSpace"/>, this method also rejects strings made up
    /// entirely of invisible Unicode characters — zero-width spaces (U+200B), BOM (U+FEFF),
    /// soft-hyphens (U+00AD), Zero Width Non-Joiner (U+200C), etc. — that some language models
    /// emit as "empty response" artefacts.
    /// </para>
    /// </summary>
    public static bool HasPrintableText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
            if (char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c))
                return true;
        return false;
    }

    public static IReadOnlyList<MarkdownBlockModel> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        // The one rule, closed blocks and an unclosed tail alike: a pattern of its own saw only the closed ones.
        content = StripThinkTags(content);
        if (string.IsNullOrWhiteSpace(content))
            return [];

        // Reject content that contains no printable characters at all
        // (e.g. a lone zero-width space U+200B that IsNullOrWhiteSpace misses).
        if (!HasPrintableText(content))
            return [];

        var doc    = Markdown.Parse(content, _pipeline);
        var result = new List<MarkdownBlockModel>();

        foreach (var block in doc)
            ProcessBlock(block, result);

        return result;
    }

    private static void ProcessBlock(Block block, List<MarkdownBlockModel> result)
    {
        switch (block)
        {
            case HeadingBlock h:
                var level       = Math.Clamp(h.Level, 1, 3);
                var headingText = InlinesToPlainText(h.Inline);
                // Skip empty headings — they produce a TextBlock with non-zero margin but no
                // visible text, which the user sees as a blank / empty bubble.
                if (!string.IsNullOrWhiteSpace(headingText))
                    result.Add(new MarkdownBlockModel { Type = $"heading{level}", Text = headingText });
                break;

            case ParagraphBlock p:
                var pb = new MarkdownBlockModel { Type = "paragraph", Text = InlinesToPlainText(p.Inline) };
                PopulateInlines(pb, p.Inline, bold: false, italic: false);
                if (pb.Inlines.Count > 0)
                    result.Add(pb);
                break;

            case FencedCodeBlock fcb:
            {
                var codeText = fcb.Lines.ToString().TrimEnd();
                // Skip empty code fences — they render as a bordered-but-empty TextBox which
                // looks like a blank bubble when the model produces only ``` ... ```.
                if (!string.IsNullOrEmpty(codeText))
                    result.Add(new MarkdownBlockModel
                    {
                        Type     = "code_block",
                        Text     = codeText,
                        Language = fcb.Info?.Trim() ?? ""
                    });
                break;
            }

            case CodeBlock cb:
            {
                var codeText = cb.Lines.ToString().TrimEnd();
                if (!string.IsNullOrEmpty(codeText))
                    result.Add(new MarkdownBlockModel { Type = "code_block", Text = codeText });
                break;
            }

            // HTML blocks (e.g. <summary>…</summary>, <remarks>…</remarks> that models sometimes
            // emit when explaining files with XML doc comments).  Without this case they were
            // silently dropped, leaving HasBlocks = false and the raw Content TextBlock visible —
            // or, worse, leaving other blocks intact (HasBlocks = true) but with a gap where the
            // HTML content should have appeared.
            // Render as a code block so the markup is clearly shown and not confused with prose.
            case HtmlBlock html:
            {
                var htmlText = html.Lines.ToString().TrimEnd();
                if (!string.IsNullOrEmpty(htmlText) && HasPrintableText(htmlText))
                    result.Add(new MarkdownBlockModel { Type = "code_block", Text = htmlText });
                break;
            }

            case ListBlock list:
                ProcessList(list, result, depth: 0);
                break;

            case ThematicBreakBlock:
                result.Add(new MarkdownBlockModel { Type = "separator" });
                break;

            case QuoteBlock quote:
                foreach (var inner in quote)
                    ProcessBlock(inner, result);
                break;

            case Table table:
                foreach (var tableRow in table.OfType<TableRow>())
                {
                    var mb = new MarkdownBlockModel { Type = tableRow.IsHeader ? "table_header_row" : "table_data_row" };
                    foreach (var cell in tableRow.OfType<Markdig.Extensions.Tables.TableCell>())
                    {
                        var sb = new StringBuilder();
                        foreach (var b in cell)
                            if (b is ParagraphBlock p)
                                sb.Append(InlinesToPlainText(p.Inline));
                        mb.Cells.Add(new TableCellModel
                        {
                            Text     = sb.ToString().Trim(),
                            IsHeader = tableRow.IsHeader,
                        });
                    }
                    if (mb.Cells.Count > 0)
                        result.Add(mb);
                }
                break;
        }
    }

    // ── Inline population ─────────────────────────────────────────────────────

    private static void PopulateInlines(MarkdownBlockModel block, ContainerInline? inlines, bool bold, bool italic)
    {
        if (inlines is null) return;
        foreach (var inline in inlines)
            AddInline(block, inline, bold, italic);
    }

    private static void AddInline(MarkdownBlockModel block, Inline inline, bool bold, bool italic)
    {
        switch (inline)
        {
            case LiteralInline lit:
                var text = lit.Content.ToString();
                if (!string.IsNullOrEmpty(text))
                    block.Inlines.Add(new InlineRunModel { Text = text, IsBold = bold, IsItalic = italic });
                break;

            case EmphasisInline em:
                // DelimiterCount: 2 = bold (**), 1 = italic (*)
                var isBold   = em.DelimiterCount >= 2 || bold;
                var isItalic = em.DelimiterCount == 1 || italic;
                foreach (var child in em)
                    AddInline(block, child, isBold, isItalic);
                break;

            case CodeInline code:
                if (!string.IsNullOrEmpty(code.Content))
                    block.Inlines.Add(new InlineRunModel { Text = code.Content, IsCode = true });
                break;

            case LineBreakInline lb:
                block.Inlines.Add(new InlineRunModel { Text = lb.IsHard ? "\n" : " ", IsBold = bold, IsItalic = italic });
                break;

            case LinkInline link:
                foreach (var child in link)
                    AddInline(block, child, bold, italic);
                break;

            case ContainerInline container:
                foreach (var child in container)
                    AddInline(block, child, bold, italic);
                break;
        }
    }

    /// <summary>
    /// Emits one block per list item, recursing into nested content. Each item's own line is built
    /// from its paragraph(s); nested sub-lists and other child blocks (code fences, …) are emitted
    /// as their own blocks afterwards — previously any nested <see cref="ListBlock"/> inside an item
    /// was silently dropped, so detail that a model formatted as sub-bullets under a "<b>Title</b> :"
    /// header vanished, leaving only the bare header line visible.
    /// </summary>
    private static void ProcessList(ListBlock list, List<MarkdownBlockModel> result, int depth)
    {
        // U+00A0 (non-breaking space) so WPF does not collapse the leading indentation.
        var indent = new string(' ', depth * 3);
        var idx    = 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var blockType = list.IsOrdered ? "numbered_item" : "bullet_item";
            var prefix    = list.IsOrdered ? $"{idx++}." : "•";

            var lb = new MarkdownBlockModel { Type = blockType, Text = $"{indent}{prefix} {GetListItemPlainText(item)}" };
            lb.Inlines.Add(new InlineRunModel { Text = $"{indent}{prefix} " });

            // Item's own line: inlines from its paragraph(s), separated by a soft break when the
            // item is "loose" (multiple paragraphs).
            var firstParagraph = true;
            foreach (var b in item)
            {
                if (b is not ParagraphBlock p) continue;
                if (!firstParagraph)
                    lb.Inlines.Add(new InlineRunModel { Text = "\n" });
                PopulateInlines(lb, p.Inline, bold: false, italic: false);
                firstParagraph = false;
            }

            result.Add(lb);

            // Nested content that does not belong to the item's own line.
            foreach (var b in item)
            {
                switch (b)
                {
                    case ParagraphBlock:
                        break; // already consumed above
                    case ListBlock nested:
                        ProcessList(nested, result, depth + 1);
                        break;
                    default:
                        ProcessBlock(b, result);
                        break;
                }
            }
        }
    }

    // ── Plain-text fallback (used for headings and Text property) ─────────────

    private static string GetListItemPlainText(ListItemBlock item)
    {
        var sb = new StringBuilder();
        foreach (var b in item)
        {
            if (b is ParagraphBlock p)
                sb.Append(InlinesToPlainText(p.Inline));
        }
        return sb.ToString().Trim();
    }

    private static string InlinesToPlainText(ContainerInline? inlines)
    {
        if (inlines is null) return "";
        var sb = new StringBuilder();
        foreach (var inline in inlines)
            AppendPlainText(sb, inline);
        return sb.ToString();
    }

    private static void AppendPlainText(StringBuilder sb, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                sb.Append(lit.Content.ToString());
                break;

            case EmphasisInline em:
                foreach (var child in em)
                    AppendPlainText(sb, child);
                break;

            case CodeInline code:
                sb.Append(code.Content);
                break;

            case LineBreakInline lb:
                sb.Append(lb.IsHard ? '\n' : ' ');
                break;

            case LinkInline link:
                foreach (var child in link)
                    AppendPlainText(sb, child);
                break;

            case ContainerInline container:
                foreach (var child in container)
                    AppendPlainText(sb, child);
                break;
        }
    }
}
