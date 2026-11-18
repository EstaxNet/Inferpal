using System.Globalization;
using System.Xml;
using Inferpal.Localization;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Documents;

/// <summary>
/// An SVG drawn to a PNG for a model that sees images, by the browser <c>view_page</c> uses (Edge, Chrome, Firefox, or
/// macOS' WebKit), with no network. A model that does not see gets its markup, as text: the text of an SVG names its
/// shapes and its labels, more than the OCR of its drawing would.
/// </summary>
/// <remarks>⚠ Drawn inside an <c>&lt;img&gt;</c>, never opened as a page: an SVG's scripts and the files it links do not run
/// or load there. ⚠ Its size is the file's own (width and height, else its viewBox), scaled so that a small icon is
/// readable and a large drawing fits what a model reads.</remarks>
internal static class SvgImage
{
    /// <summary>The width a drawing is brought to when it is narrower, and the most it is given.</summary>
    private const int MinWidth = 800, MaxWidth = 1920, MaxHeight = 4096;

    /// <summary>The namespace a browser draws an SVG document in: a root outside it is shown as a broken image.</summary>
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    /// <summary>Whether <paramref name="path"/> names an SVG, by its extension.</summary>
    internal static bool Handles(string path) => Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>The size the SVG is drawn at: its own (width and height, else its viewBox, else a browser's 300 × 150),
    /// scaled between <see cref="MinWidth"/> and <see cref="MaxWidth"/> wide, at most <see cref="MaxHeight"/> high.</summary>
    internal static (int Width, int Height) DrawnSize(byte[] svg)
    {
        double width = 300, height = 150;
        try
        {
            // An internal DTD is read (Illustrator's entities name the root's own attributes), nothing outside is fetched
            // (no resolver), and what its entities expand to is bounded.
            using var reader = XmlReader.Create(new MemoryStream(svg),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null, MaxCharactersFromEntities = 100_000 });
            if (reader.MoveToContent() == XmlNodeType.Element && reader.LocalName == "svg")
            {
                var w = Length(reader.GetAttribute("width"));
                var h = Length(reader.GetAttribute("height"));
                var box = (reader.GetAttribute("viewBox") ?? string.Empty)
                    .Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
                var (bw, bh) = box.Length == 4 && box[2] > 0 && box[3] > 0 ? (box[2], box[3]) : (double.NaN, double.NaN);
                (width, height) = (w, h) switch
                {
                    ({ } a, { } b) => (a, b),
                    ({ } a, null) when !double.IsNaN(bw) => (a, a * bh / bw),
                    (null, { } b) when !double.IsNaN(bw) => (b * bw / bh, b),
                    _ when !double.IsNaN(bw) => (bw, bh),
                    _ => (width, height),
                };
            }
        }
        catch (XmlException ex) { Diagnostics.Swallow("SvgImage.Size", ex); }
        if (width <= 0 || height <= 0 || !double.IsFinite(width) || !double.IsFinite(height)) (width, height) = (300, 150);
        var scale = width < MinWidth ? Math.Min(8, MinWidth / width) : width > MaxWidth ? MaxWidth / width : 1;
        if (height * scale > MaxHeight) scale = MaxHeight / height;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// Whether a browser draws the file as an SVG document: well-formed XML, with namespaces, its root the SVG namespace's
    /// <c>svg</c>. An SVG copied from HTML (no <c>xmlns</c>), an undeclared prefix or an unescaped <c>&amp;</c> is shown as
    /// a broken image inside an <c>&lt;img&gt;</c> — never a drawing to send.
    /// </summary>
    internal static bool IsDrawable(byte[] svg)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(svg),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null, MaxCharactersFromEntities = 100_000 });
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "svg" || reader.NamespaceURI != SvgNamespace)
                return false;
            while (reader.Read()) { }
            return true;
        }
        catch (XmlException) { return false; }
    }

    /// <summary>A length as a browser reads it, in CSS pixels; <c>null</c> for a percentage or nothing.</summary>
    private static double? Length(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        var unit = new string(v.Reverse().TakeWhile(char.IsLetter).Reverse().ToArray());
        if (v.EndsWith('%')) return null;
        if (!double.TryParse(v[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0) return null;
        return unit.ToLowerInvariant() switch
        {
            "" or "px" => n,
            "pt" => n * 96 / 72,
            "pc" => n * 16,
            "in" => n * 96,
            "cm" => n * 96 / 2.54,
            "mm" => n * 96 / 25.4,
            "em" => n * 16,
            _ => null,
        };
    }

    /// <summary>
    /// What an SVG the person attaches becomes: drawn, for a model that <paramref name="sees"/> images, when a browser draws
    /// it; its markup otherwise — and its chip's line says which (a model that does not see, or not known to: the markup).
    /// </summary>
    /// <returns>The drawing named as the file, or <c>null</c> (the markup goes), and the chip's line.</returns>
    internal static async Task<(ChatImage? Drawn, string Detail)> AttachAsync(byte[] svg, string name, bool? sees, CancellationToken ct)
    {
        var markup = MarkupChip(svg.Length);
        // Past the attachments' size, never drawn: a browser would rasterize hundreds of megabytes.
        if (sees != true || svg.Length > ChatImages.MaxBytes) return (null, markup);
        if (!IsDrawable(svg)) return (null, Strings.SvgNotDrawnChip);
        var (png, width, height, why) = await DrawAsync(svg, ct).ConfigureAwait(false);
        if (png is null)
        {
            Diagnostics.Record("SvgImage.Attach", why ?? "not drawn");
            return (null, why == NoBrowser ? Strings.SvgNoBrowserChip : Strings.SvgNotDrawnChip);
        }
        // The size the model gets: the reader reduces a drawing past 1920 × 1080, and the chip says that size, not the drawn one.
        var read = ChatImages.Read(png, name);
        return read.Image is { } image ? (image, Strings.SvgDrawnChip(image.Width, image.Height)) : (null, Strings.SvgNotDrawnChip);
    }

    /// <summary>The chip's line of an SVG sent as its markup, by its size in bytes.</summary>
    internal static string MarkupChip(long bytes) => Strings.SvgMarkupChip(Math.Max(1, (int)Math.Round(bytes / 1024.0)));

    /// <summary>Why an SVG is not drawn on a machine with no browser to draw it.</summary>
    private const string NoBrowser = "no browser to draw it";

    /// <summary>The SVG drawn as a PNG, its size, or why it could not be (in English): no browser on this machine, the
    /// ones there failed or did not start, or the drawing shows nothing.</summary>
    /// <remarks>One budget for every browser: after one runs out of time, the next is not tried (a heavy drawing would
    /// hold the attachment for minutes, browser after browser).</remarks>
    internal static async Task<(byte[]? Png, int Width, int Height, string? Why)> DrawAsync(byte[] svg, CancellationToken ct)
    {
        var browsers = PageCapture.FindAll().ToList();
        if (browsers.Count == 0) return (null, 0, 0, NoBrowser);
        var (width, height) = DrawnSize(svg);
        var dir = Directory.CreateTempSubdirectory("inferpal-svg-");
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir.FullName, "image.svg"), svg, ct).ConfigureAwait(false);
            var inv = CultureInfo.InvariantCulture;
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "page.html"),
                "<!doctype html><html><head><meta charset=\"utf-8\"><style>html,body{margin:0;background:#fff}"
                + string.Create(inv, $"img{{display:block;width:{width}px;height:{height}px}}</style></head>")
                + "<body><img src=\"image.svg\" alt=\"\"></body></html>", ct).ConfigureAwait(false);
            using var pages = WorkspacePages.Start(dir.FullName);
            var failures = new List<string>();
            var started = DateTime.UtcNow;
            foreach (var browser in browsers)
            {
                if (DateTime.UtcNow - started >= PageCapture.Budget) break;
                byte[]? png;
                string? why;
                // A browser that cannot start (blocked, removed since it was found) is one failure: the next is tried.
                try { (png, why) = await PageCapture.CaptureAsync(browser, pages.UrlOf("page.html"), width, ct, height).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { (png, why) = (null, browser.Name + ": " + ex.Message); }
                if (png is not null)
                    return IsBlank(png) ? (null, 0, 0, "the drawing shows nothing") : (png, width, height, null);
                failures.Add(why ?? browser.Name + " failed");
            }
            return (null, 0, 0, "the browser could not draw it: " + string.Join("; ", failures));
        }
        // The page's server or the temporary files: the markup goes, said.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.HttpListenerException or System.Net.Sockets.SocketException
                                   or InvalidOperationException)
        {
            return (null, 0, 0, "it could not be drawn here: " + ex.Message);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { /* cleanup */ }
        }
    }

    /// <summary>Whether a capture is one colour: an SVG made of symbols only, or hidden, draws nothing worth sending.</summary>
    internal static bool IsBlank(byte[] png)
    {
        try
        {
            var image = StbImageSharp.ImageResult.FromMemory(png, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            var pixels = MemoryMarshalCast(image.Data);
            for (int i = 1; i < pixels.Length; i++)
                if (pixels[i] != pixels[0]) return false;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("SvgImage.Blank", ex);
            return false;
        }
    }

    private static ReadOnlySpan<int> MemoryMarshalCast(byte[] rgba) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(rgba);
}
