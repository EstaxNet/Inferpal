using System.Text.Json;
using Inferpal.Services.Documents;

namespace Inferpal.Services.Tools;

/// <summary>
/// Agent tool: the text written in an image of the workspace (a screenshot, a photo of a page) or in a scanned PDF, read by the
/// OCR built into Inferpal — every system, the product's ten languages (Latin, Chinese, Cyrillic, Japanese, Korean), tables
/// put back in rows. For a model that does not read images, and for one that does when it needs the exact text (an error
/// message to search for, a value to copy) rather than a picture.
/// </summary>
/// <remarks>Read only: what an image or a scan holds, through the same engine as a scanned PDF (<see cref="PdfOcr"/>). The
/// path is confined to the workspace and the ignore files' exclusions apply as for any reading tool (the registry's
/// funnel).</remarks>
internal sealed class OcrTool : ITool
{
    private readonly Func<string> _getWorkspaceRoot;

    public OcrTool(Func<string> getWorkspaceRoot) => _getWorkspaceRoot = getWorkspaceRoot;

    public string Name => "ocr";

    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Reads;

    public string Description =>
        "Read the text written in an image of the workspace (PNG, JPEG, BMP, GIF: a screenshot of an error, a photo of a page) or " +
        "in a scanned PDF, by OCR — Latin, Chinese, Cyrillic, Japanese and Korean alphabets; tables come back row by row. Use it " +
        "when you need the exact words of an image: an error message to search for, a value to copy. The text can hold misreadings.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "The image or the scanned PDF, relative to the workspace root." },
        },
        required = new[] { "path" },
    };

    /// <summary>The image formats the engine decodes.</summary>
    internal static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var root = _getWorkspaceRoot();
        var path = PathSanitizer.Sanitize(args.Str("path"), root);
        PathSanitizer.AssertUnderRoot(path, root);
        var name = Path.GetFileName(path);
        if (Directory.Exists(path)) return $"'{name}' is a folder: name an image or a scanned PDF in it.";
        if (!File.Exists(path)) return ToolFailure.Said(Localization.Strings.ToolFileNotFound(path));

        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var scan = await Task.Run(() => AttachedDocuments.IsScan(path) ? AttachedDocuments.Scan(path, ct) : null, ct);
            if (scan is null)
                return AttachedDocuments.WhyNoText(path) is { } why
                    ? $"'{name}' cannot be read: {why}."
                    : $"'{name}' holds text of its own: read it with read_file (OCR reads only a PDF made of scanned images).";
            return scan.Status switch
            {
                OcrStatus.Read => AttachedDocuments.ScanText(name, scan),
                OcrStatus.Empty => $"'{name}' is a scan, and the OCR found no text in it: a photo, a drawing or a blank page.",
                _ => $"'{name}' is a scan the OCR could not read: {scan.Why ?? "the reading failed"}.",
            };
        }

        if (Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase))
            return $"'{name}' is a WebP image, a format this OCR does not decode: a model that reads images can be shown it with read_file.";
        if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return $"'{name}' is not an image the OCR reads ({string.Join(", ", Extensions)}) nor a scanned PDF: read it with read_file.";
        if (new FileInfo(path).Length > ChatImages.MaxBytes)
            return $"'{name}' is an image larger than {ChatImages.MaxBytes / (1024 * 1024)} MB: the OCR does not read it.";
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var reading = await Task.Run(() => PdfOcr.Picture(bytes, ct), ct);
        return reading.Status switch
        {
            OcrStatus.Read => $"[Text read by OCR ({reading.Engine}) from {name}, an image: words and figures can be misread — check " +
                              $"what matters against the image; tables are rebuilt row by row, cells between \"|\".]\n\n{reading.Text}",
            OcrStatus.Empty => $"'{name}' holds no text the OCR finds: a photo, a drawing, or text too small to read.",
            _ => $"The OCR could not read '{name}': {reading.Why ?? reading.Skipped ?? "the reading failed"}.",
        };
    }
}
