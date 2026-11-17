namespace Inferpal.Services.Documents;

/// <summary>
/// The words written in an image, for a model that does not read images (pari 108): read by the OCR built into Inferpal (the
/// product's ten languages, every system) and given to the model instead of the picture — said as read by OCR, which can
/// misread. One reader for the three ways an image reaches a model: <c>read_file</c>, an image attached in either editor.
/// </summary>
internal static class ImageText
{
    // What the OCR read of an image, by its bytes: the chip's check runs at every attach and model change, the send and every
    // regenerate again — the text does not depend on the model, and a reading costs a second or more.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, OcrReading?> Known = new(StringComparer.Ordinal);

    /// <summary>The text of <paramref name="image"/>; <c>null</c> when the OCR found none or could not read it (a photo, a
    /// drawing, an alphabet it does not read). ⚠ Seconds at most: from a worker, never an editor's thread.</summary>
    public static OcrReading? Read(ChatImage image, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(image.Base64); }
        catch (FormatException) { return null; }
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (Known.TryGetValue(key, out var known)) return known;
        var reading = PdfOcr.Picture(bytes, ct);
        // A failure (out of time) is never kept: the next check reads again.
        if (reading.Status is OcrStatus.Failed or OcrStatus.Unavailable) return null;
        var text = reading is { Status: OcrStatus.Read } && reading.Text.Trim().Length > 0 ? reading : null;
        if (Known.Count > 32) Known.Clear();
        Known[key] = text;
        return text;
    }

    /// <summary>How many lines of text the reading holds (the chip says it).</summary>
    public static int Lines(OcrReading reading) =>
        reading.Text.Split('\n').Count(l => l.Trim().Length > 0 && !l.StartsWith("[Read with low confidence", StringComparison.Ordinal));

    /// <summary>The text as the model reads it in place of <paramref name="name"/>: where it comes from, that it can be
    /// misread, then the text.</summary>
    public static string ForModel(string name, OcrReading reading) =>
        $"[The text of the image {name}, read by OCR ({reading.Engine}) because the model answering does not read images — " +
        "words and figures can be misread; tables are rebuilt row by row, cells between \"|\".]\n\n" + reading.Text;
}
