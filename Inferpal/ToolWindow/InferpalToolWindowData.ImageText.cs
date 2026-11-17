using System.IO;
using System.Runtime.Serialization;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// The banner over the composer when the text of the attached images goes instead of them, for a model that does not see
/// (pari 108, mockup OcrReady): what happens, "see the text read" (opened beside the chat), and "ask the server's model that
/// sees instead" (the question then goes to it, with the images).
/// </summary>
internal partial class InferpalToolWindowData
{
    /// <summary>The image chips whose text goes instead of them, with that text.</summary>
    private readonly List<(AttachmentItem Item, Services.Presentation.ImageTextModel Text)> _imageTexts = [];
    /// <summary>The model the next question answers with, as the banner was built for, and the server's models that see.</summary>
    private (string Model, IReadOnlyList<string> Vision)? _imageTextFor;
    /// <summary>The model of the server that sees, chosen on the banner for the next question: the images go to it.</summary>
    private string? _imageAskOther;

    private bool _hasImageTextBanner;
    private string _imageTextLine = string.Empty;
    private string _imageSeeTextLabel = string.Empty;
    private string _imageAskOtherLabel = string.Empty;
    private bool _hasImageAskOther;

    [DataMember] public bool   HasImageTextBanner { get => _hasImageTextBanner; set => SetProperty(ref _hasImageTextBanner, value); }
    [DataMember] public string ImageTextLine      { get => _imageTextLine;      set => SetProperty(ref _imageTextLine, value); }
    [DataMember] public string ImageSeeTextLabel  { get => _imageSeeTextLabel;  set => SetProperty(ref _imageSeeTextLabel, value); }
    [DataMember] public string ImageAskOtherLabel { get => _imageAskOtherLabel; set => SetProperty(ref _imageAskOtherLabel, value); }
    [DataMember] public bool   HasImageAskOther   { get => _hasImageAskOther;   set => SetProperty(ref _hasImageAskOther, value); }
    [DataMember] public AsyncCommand ImageSeeTextCommand  { get; private set; } = null!;
    [DataMember] public AsyncCommand ImageAskOtherCommand { get; private set; } = null!;

    private void InitImageText()
    {
        ImageSeeTextCommand  = new AsyncCommand((_, ct) => ShowImageTextsAsync(ct));
        ImageAskOtherCommand = new AsyncCommand((_, _) => RunOnVMContextAsync(() =>
        {
            var other = _imageTextFor is { } f && _imageTexts.Count > 0 ? Services.Presentation.ImageCards.TextBanner(
                f.Model, [.. _imageTexts.Select(t => t.Text)], f.Vision, [_config.DefaultModel, _config.AgentModel]).OtherModel : "";
            _imageAskOther = other.Length > 0 ? other : null;
            RefreshImageTextBanner();
        }));
    }

    /// <summary>An image chip whose text goes instead of it, for <paramref name="model"/>: kept, and the banner said.</summary>
    private void AddImageText(AttachmentItem item, Services.Presentation.ImageTextModel text, string model, IReadOnlyList<string> vision)
    {
        _imageTexts.Add((item, text));
        _imageTextFor = (model, vision);
        RefreshImageTextBanner();
    }

    /// <summary>A chip removed: its text goes with it.</summary>
    private void RemoveImageText(AttachmentItem item)
    {
        if (_imageTexts.RemoveAll(t => t.Item == item) == 0) return;
        // The last one gone, the choice made on the banner goes too: an image attached next is judged afresh.
        if (_imageTexts.Count == 0) _imageAskOther = null;
        RefreshImageTextBanner();
    }

    /// <summary>The chips left with the question: the banner goes, and the choice made on it.</summary>
    private void ClearImageTexts()
    {
        _imageTexts.Clear();
        _imageAskOther = null;
        RefreshImageTextBanner();
    }

    private void RefreshImageTextBanner()
    {
        if (_imageTexts.Count == 0 || _imageTextFor is not { } f)
        {
            HasImageTextBanner = false;
            return;
        }
        var banner = Services.Presentation.ImageCards.TextBanner(f.Model, [.. _imageTexts.Select(t => t.Text)], f.Vision,
                                                                 [_config.DefaultModel, _config.AgentModel]);
        HasImageTextBanner = true;
        // The model that sees chosen: the line says so, the buttons go.
        ImageTextLine      = _imageAskOther is null ? banner.Line : banner.OtherChosen;
        ImageSeeTextLabel  = banner.SeeLabel;
        ImageAskOtherLabel = banner.AskOtherLabel;
        HasImageAskOther   = _imageAskOther is null && banner.OtherModel.Length > 0;
    }

    /// <summary>The window's one folder for the text shown, so a click rewrites one file instead of leaving one per click.</summary>
    private string? _imageTextFolder;

    /// <summary>The text read in the chips' images, opened beside the chat as a document to read (a temporary file).</summary>
    private async Task ShowImageTextsAsync(CancellationToken ct)
    {
        var text = string.Empty;
        await RunOnVMContextAsync(() =>
            text = string.Join("\n\n---\n\n", _imageTexts.Select(t => t.Text.Label + "\n\n" + t.Text.ForModel)));
        if (text.Length == 0) return;
        try
        {
            _imageTextFolder ??= Directory.CreateTempSubdirectory("inferpal-ocr-text-").FullName;
            var path = Path.Combine(_imageTextFolder, "text-read-by-ocr.txt");
            await File.WriteAllTextAsync(path, text, ct);
            OpenInEditor(path, "ImageText.Show", ex => Localization.Strings.MsgError(ex.Message));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("ImageText.Show", ex); }
    }
}
