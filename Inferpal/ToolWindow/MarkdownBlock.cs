using System.Collections.ObjectModel;
using Inferpal.Localization;
using System.Runtime.Serialization;
using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

[DataContract]
internal sealed class MarkdownBlock : NotifyPropertyChangedObject
{
    [DataMember] public string Type     { get; init; } = "";
    [DataMember] public string Text     { get; init; } = "";
    [DataMember] public string Language { get; init; } = "";
    [DataMember] public string Marker   { get; init; } = "";

    private bool _hasInlines;
    [DataMember] public ObservableCollection<InlineRun> Inlines    { get; } = [];
    [DataMember] public bool                            HasInlines  { get => _hasInlines; set => SetProperty(ref _hasInlines, value); }

    [DataMember] public ObservableCollection<TableCell> Cells { get; } = [];

    private string _themeText        = "#E8E8EC";
    private string _themeCodeText    = "#E8E8EC";
    private string _themeCodeBg      = "#18181C";
    private string _themeCodeBorder  = "#34343C";
    private string _themeTableBorder = "#34343C";

    [DataMember] public string ThemeText        { get => _themeText;        set => SetProperty(ref _themeText,        value); }
    [DataMember] public string ThemeCodeText    { get => _themeCodeText;    set => SetProperty(ref _themeCodeText,    value); }
    [DataMember] public string ThemeCodeBg      { get => _themeCodeBg;      set => SetProperty(ref _themeCodeBg,      value); }
    [DataMember] public string ThemeCodeBorder  { get => _themeCodeBorder;  set => SetProperty(ref _themeCodeBorder,  value); }
    [DataMember] public string ThemeTableBorder { get => _themeTableBorder; set => SetProperty(ref _themeTableBorder, value); }

    [DataMember] public AsyncCommand CopyCodeCommand    { get; }
    [DataMember] public AsyncCommand SaveSnippetCommand { get; }

    public MarkdownBlock()
    {
        CopyCodeCommand    = new AsyncCommand(CopyCodeAsync);
        SaveSnippetCommand = new AsyncCommand(SaveSnippetAsync);
    }

    /// <summary>
    /// Maps the editor-agnostic parse result (<see cref="Services.Presentation.MarkdownParser"/>)
    /// to this Remote-UI observable type. Pure mapping — theming is applied afterwards by the
    /// owning <see cref="ChatMessageItem"/> which knows the current VS theme.
    /// </summary>
    internal static MarkdownBlock FromModel(MarkdownBlockModel model)
    {
        var block = new MarkdownBlock
        {
            Type       = model.Type,
            Text       = model.Text,
            Language   = model.Language,
            Marker     = model.Marker,
            HasInlines = model.HasInlines,
        };
        // One child per word: the WrapPanel that lays them out only ends a line between two children.
        foreach (var piece in InlineFlow.Pieces(model.Inlines))
            block.Inlines.Add(new InlineRun
            {
                Text = piece.Text, IsBold = piece.IsBold, IsItalic = piece.IsItalic, IsCode = piece.IsCode,
                IsBreak = piece.IsBreak, StartsSpan = piece.StartsSpan, EndsSpan = piece.EndsSpan,
            });
        foreach (var cell in model.Cells)
            block.Cells.Add(new TableCell { Text = cell.Text, IsHeader = cell.IsHeader });
        return block;
    }

    private Task CopyCodeAsync(object? _, CancellationToken ct)
    {
        ClipboardHelper.TrySet(Text, "Clipboard.CopyCodeBlock");
        return Task.CompletedTask;
    }

    // The store returns false when nothing was written: the button shows the outcome instead of staying
    // silent (MarkdownBlock has no link to the conversation). Segoe MDL2 glyphs: star, check, error.
    private const string GlyphSaveSnippet = "";
    private const string GlyphSaved       = "";
    private const string GlyphNotSaved    = "";

    private string _saveSnippetGlyph = GlyphSaveSnippet;
    [DataMember] public string SaveSnippetGlyph { get => _saveSnippetGlyph; set => SetProperty(ref _saveSnippetGlyph, value); }

    // The block's own tooltip: what the button does, and once clicked, why it did not save.
    private string _saveSnippetTip = Strings.TooltipSaveSnippet;
    [DataMember] public string SaveSnippetTip { get => _saveSnippetTip; set => SetProperty(ref _saveSnippetTip, value); }

    private async Task SaveSnippetAsync(object? _, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Text)) return;
        var result = await Inferpal.Services.Persistence.SnippetStore.SaveAsync(Language, Text, ct);
        SaveSnippetGlyph = result == Inferpal.Services.Persistence.SnippetSaveResult.Saved ? GlyphSaved : GlyphNotSaved;
        SaveSnippetTip   = result switch
        {
            Inferpal.Services.Persistence.SnippetSaveResult.Full       => Strings.SnippetsFull(Inferpal.Services.Persistence.SnippetStore.MaxSnippets),
            Inferpal.Services.Persistence.SnippetSaveResult.NotWritten => Strings.SnippetsWriteFailed,
            _                                                          => Strings.TooltipSaveSnippet,
        };
    }
}
