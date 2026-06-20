using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Inferpal.ToolWindow;

/// <summary>
/// Enriched TextBlock that parses a subset of inline Markdown
/// (**bold**, *italic*, `code`) and populates its WPF Inlines.
/// Parsing runs in the VS process (where this type is loaded for the Remote UI).
/// </summary>
public class MarkdownTextBlock : TextBlock
{
    public static readonly DependencyProperty InlineMarkdownProperty =
        DependencyProperty.Register(
            "InlineMarkdown",
            typeof(string),
            typeof(MarkdownTextBlock),
            new PropertyMetadata(string.Empty, OnInlineMarkdownChanged));

    public string InlineMarkdown
    {
        get => (string)GetValue(InlineMarkdownProperty);
        set => SetValue(InlineMarkdownProperty, value);
    }

    private static void OnInlineMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;

        tb.Inlines.Clear();

        if (e.NewValue is not string md || string.IsNullOrEmpty(md))
            return;

        foreach (var inline in ParseInlines(md))
            tb.Inlines.Add(inline);
    }

    /// <summary>
    /// Lightweight inline parser: `code`, ***bold+italic***, **bold**, *italic*, \n.
    /// No Markdig — works on plain strings, with no extra dependency inside VS.
    /// </summary>
    private static List<Inline> ParseInlines(string md)
    {
        var result    = new List<Inline>();
        int i         = 0;
        int textStart = 0;

        void Flush(int end)
        {
            if (end > textStart)
                result.Add(new Run(md[textStart..end]));
            textStart = end;
        }

        while (i < md.Length)
        {
            char c = md[i];

            // ── Inline code : `code` ───────────────────────────────────────────
            if (c == '`')
            {
                int end = md.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush(i);
                    result.Add(new Run(md[(i + 1)..end])
                    {
                        FontFamily = new FontFamily("Consolas, Courier New"),
                        Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                    });
                    i = textStart = end + 1;
                    continue;
                }
            }

            // ── Bold + italic : ***text*** ─────────────────────────────────────
            if (c == '*' && i + 2 < md.Length && md[i + 1] == '*' && md[i + 2] == '*')
            {
                int end = md.IndexOf("***", i + 3, StringComparison.Ordinal);
                if (end > i)
                {
                    Flush(i);
                    result.Add(new Bold(new Italic(new Run(md[(i + 3)..end]))));
                    i = textStart = end + 3;
                    continue;
                }
            }

            // ── Bold : **text** ────────────────────────────────────────────────
            if (c == '*' && i + 1 < md.Length && md[i + 1] == '*')
            {
                int end = md.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    Flush(i);
                    result.Add(new Bold(new Run(md[(i + 2)..end])));
                    i = textStart = end + 2;
                    continue;
                }
            }

            // ── Italic : *text* ────────────────────────────────────────────────
            if (c == '*')
            {
                int end = md.IndexOf('*', i + 1);
                if (end > i)
                {
                    Flush(i);
                    result.Add(new Italic(new Run(md[(i + 1)..end])));
                    i = textStart = end + 1;
                    continue;
                }
            }

            // ── Saut de ligne ──────────────────────────────────────────────────
            if (c == '\n')
            {
                Flush(i);
                result.Add(new LineBreak());
                i = textStart = i + 1;
                continue;
            }

            i++;
        }

        Flush(md.Length);
        return result;
    }
}
