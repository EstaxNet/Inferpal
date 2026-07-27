namespace Inferpal.Services.Presentation;

/// <summary>
/// Editor-agnostic result of <see cref="MarkdownParser.Parse"/>: one rendered chat block
/// (paragraph, heading, code fence, list item, table row, separator…). Plain data, no VS
/// dependency — the tool window maps these to its Remote-UI observable types
/// (<c>Inferpal.ToolWindow.MarkdownBlock</c>) which carry theming and commands.
/// </summary>
internal sealed class MarkdownBlockModel
{
    /// <summary>Block kind consumed by the UI templates: "paragraph", "heading1".."heading3",
    /// "code_block", "bullet_item", "numbered_item", "separator", "table_header_row", "table_data_row".</summary>
    public string Type { get; init; } = "";

    /// <summary>Plain-text fallback of the block (also the copyable text for code blocks).</summary>
    public string Text { get; init; } = "";

    /// <summary>Fence info string for code blocks ("cs", "json", …), empty otherwise.</summary>
    public string Language { get; init; } = "";

    /// <summary>Formatted inline runs for paragraphs and list items.</summary>
    public List<InlineRunModel> Inlines { get; } = [];

    /// <summary>Cells for table rows, empty for every other block type.</summary>
    public List<TableCellModel> Cells { get; } = [];

    /// <summary>True when the block carries formatted inline runs (paragraphs, list items).</summary>
    public bool HasInlines => Inlines.Count > 0;
}

/// <summary>A run of text with uniform formatting inside a <see cref="MarkdownBlockModel"/>.</summary>
internal sealed class InlineRunModel
{
    public string Text     { get; init; } = "";
    public bool   IsBold   { get; init; }
    public bool   IsItalic { get; init; }
    public bool   IsCode   { get; init; }
}

/// <summary>A single cell of a Markdown table row.</summary>
internal sealed class TableCellModel
{
    public string Text     { get; init; } = "";
    public bool   IsHeader { get; init; }
}
