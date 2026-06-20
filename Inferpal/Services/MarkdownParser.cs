using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Inferpal.ToolWindow;

namespace Inferpal.Services;

internal static class MarkdownParser
{
    private static readonly MarkdownPipeline _pipeline =
        new MarkdownPipelineBuilder()
            .UseEmphasisExtras()
            .UseAutoLinks()
            .UsePipeTables()
            .Build();

    private static readonly Regex _thinkTagRegex =
        new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Removes all <c>&lt;think&gt;...&lt;/think&gt;</c> blocks from <paramref name="content"/>
    /// and trims the result. Returns an empty string when the input is null or whitespace.
    /// </summary>
    public static string StripThinkTags(string? content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        return _thinkTagRegex.Replace(content, "").Trim();
    }

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

    public static IReadOnlyList<MarkdownBlock> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        content = _thinkTagRegex.Replace(content, "").Trim();
        if (string.IsNullOrWhiteSpace(content))
            return [];

        // Reject content that contains no printable characters at all
        // (e.g. a lone zero-width space U+200B that IsNullOrWhiteSpace misses).
        if (!HasPrintableText(content))
            return [];

        var doc    = Markdown.Parse(content, _pipeline);
        var result = new List<MarkdownBlock>();

        foreach (var block in doc)
            ProcessBlock(block, result);

        return result;
    }

    private static void ProcessBlock(Block block, List<MarkdownBlock> result)
    {
        switch (block)
        {
            case HeadingBlock h:
                var level       = Math.Clamp(h.Level, 1, 3);
                var headingText = InlinesToPlainText(h.Inline);
                // Skip empty headings — they produce a TextBlock with non-zero margin but no
                // visible text, which the user sees as a blank / empty bubble.
                if (!string.IsNullOrWhiteSpace(headingText))
                    result.Add(new MarkdownBlock { Type = $"heading{level}", Text = headingText });
                break;

            case ParagraphBlock p:
                var pb = new MarkdownBlock { Type = "paragraph", Text = InlinesToPlainText(p.Inline) };
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
                    result.Add(new MarkdownBlock
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
                    result.Add(new MarkdownBlock { Type = "code_block", Text = codeText });
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
                    result.Add(new MarkdownBlock { Type = "code_block", Text = htmlText });
                break;
            }

            case ListBlock list:
                ProcessList(list, result, depth: 0);
                break;

            case ThematicBreakBlock:
                result.Add(new MarkdownBlock { Type = "separator" });
                break;

            case QuoteBlock quote:
                foreach (var inner in quote)
                    ProcessBlock(inner, result);
                break;

            case Table table:
                foreach (var tableRow in table.OfType<TableRow>())
                {
                    var mb = new MarkdownBlock { Type = tableRow.IsHeader ? "table_header_row" : "table_data_row" };
                    foreach (var cell in tableRow.OfType<Markdig.Extensions.Tables.TableCell>())
                    {
                        var sb = new StringBuilder();
                        foreach (var b in cell)
                            if (b is ParagraphBlock p)
                                sb.Append(InlinesToPlainText(p.Inline));
                        mb.Cells.Add(new Inferpal.ToolWindow.TableCell
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

    private static void PopulateInlines(MarkdownBlock block, ContainerInline? inlines, bool bold, bool italic)
    {
        if (inlines is null) return;
        foreach (var inline in inlines)
            AddInline(block, inline, bold, italic);
        block.HasInlines = block.Inlines.Count > 0;
    }

    private static void AddInline(MarkdownBlock block, Inline inline, bool bold, bool italic)
    {
        switch (inline)
        {
            case LiteralInline lit:
                var text = lit.Content.ToString();
                if (!string.IsNullOrEmpty(text))
                    block.Inlines.Add(new InlineRun { Text = text, IsBold = bold, IsItalic = italic });
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
                    block.Inlines.Add(new InlineRun { Text = code.Content, IsCode = true });
                break;

            case LineBreakInline lb:
                block.Inlines.Add(new InlineRun { Text = lb.IsHard ? "\n" : " ", IsBold = bold, IsItalic = italic });
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
    private static void ProcessList(ListBlock list, List<MarkdownBlock> result, int depth)
    {
        // U+00A0 (non-breaking space) so WPF does not collapse the leading indentation.
        var indent = new string(' ', depth * 3);
        var idx    = 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var blockType = list.IsOrdered ? "numbered_item" : "bullet_item";
            var prefix    = list.IsOrdered ? $"{idx++}." : "•";

            var lb = new MarkdownBlock { Type = blockType, Text = $"{indent}{prefix} {GetListItemPlainText(item)}" };
            lb.Inlines.Add(new InlineRun { Text = $"{indent}{prefix} " });

            // Item's own line: inlines from its paragraph(s), separated by a soft break when the
            // item is "loose" (multiple paragraphs).
            var firstParagraph = true;
            foreach (var b in item)
            {
                if (b is not ParagraphBlock p) continue;
                if (!firstParagraph)
                    lb.Inlines.Add(new InlineRun { Text = "\n" });
                PopulateInlines(lb, p.Inline, bold: false, italic: false);
                firstParagraph = false;
            }

            lb.HasInlines = lb.Inlines.Count > 0;
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
