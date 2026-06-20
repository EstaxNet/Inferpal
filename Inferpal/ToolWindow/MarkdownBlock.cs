using System.Collections.ObjectModel;
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

    private bool _hasInlines;
    [DataMember] public ObservableCollection<InlineRun> Inlines    { get; } = [];
    [DataMember] public bool                            HasInlines  { get => _hasInlines; set => SetProperty(ref _hasInlines, value); }

    [DataMember] public ObservableCollection<TableCell> Cells { get; } = [];

    private string _themeText        = "#D4D4D4";
    private string _themeCodeText    = "#CE9178";
    private string _themeCodeBg      = "#161616";
    private string _themeCodeBorder  = "#333333";
    private string _themeTableBorder = "#3F3F46";

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

    private Task CopyCodeAsync(object? _, CancellationToken ct)
    {
        var text = Text;
        var thread = new Thread(() =>
        {
            try { System.Windows.Clipboard.SetText(string.IsNullOrEmpty(text) ? " " : text); }
            catch { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return Task.CompletedTask;
    }

    private async Task SaveSnippetAsync(object? _, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Text)) return;
        await Inferpal.Services.Persistence.SnippetStore.SaveAsync(Language, Text, ct);
    }
}
