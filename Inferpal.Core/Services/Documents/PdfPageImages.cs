namespace Inferpal.Services.Documents;

/// <summary>A scanned page's picture, as the PDF holds it: a JPEG as it is, or raw pixels written as a PNG.</summary>
/// <param name="Page">The page's number from 1.</param>
internal sealed record PdfPageImage(int Page, byte[] Bytes, string Extension);

/// <summary>
/// The pictures of a scanned PDF's pages — on every system, with no renderer: a scanner writes each page as one image, which
/// the PDF keeps as it is (a JPEG, <c>DCTDecode</c>) or as compressed pixels (<c>FlateDecode</c>). What an OCR without a PDF
/// renderer reads (<see cref="PdfOcr"/>), and what a model that sees is shown when no OCR reads it.
/// </summary>
/// <remarks>Black-and-white fax encodings (<c>CCITTFaxDecode</c>, <c>JBIG2Decode</c>) and JPEG 2000 (<c>JPXDecode</c>) need a
/// decoder this reader does not have: such a page is counted in <see cref="Extraction.NotDecoded"/> and said.</remarks>
internal static class PdfPageImages
{
    /// <summary>What was taken: the pictures, the pages whose picture is in a form not decoded here, and the page count.</summary>
    internal sealed record Extraction(IReadOnlyList<PdfPageImage> Images, int NotDecoded, int Pages);

    /// <summary>The largest picture each page holds, for the first <paramref name="maxPages"/> pages — never one an earlier page
    /// took (a producer that puts every page's picture in resources the pages share). Past the decoding budget, the pages
    /// decoded so far.</summary>
    internal static Extraction Extract(byte[] bytes, int maxPages, CancellationToken ct, string? password = null)
    {
        var doc = new PdfDocument(bytes, ct, password);
        if (doc.Locked || doc.UnsupportedEncryption) return new([], 0, 0);
        var root = doc.Dict(doc.Trailer.GetValueOrDefault("Root"));
        var pages = new List<Dictionary<string, object?>>();
        Collect(doc, root?.GetValueOrDefault("Pages"), pages, [], 0);
        var images = new List<PdfPageImage>();
        var used = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        var notDecoded = 0;
        for (var i = 0; i < pages.Count && i < maxPages; i++)
        {
            ct.ThrowIfCancellationRequested();
            var resources = doc.Dict(Inherited(doc, pages[i], "Resources"));
            var largest = Images(doc, resources, 0).Where(s => !used.Contains(s))
                                                   .OrderByDescending(s => doc.Num(s.Dict.GetValueOrDefault("Width")) * doc.Num(s.Dict.GetValueOrDefault("Height")))
                                                   .FirstOrDefault();
            if (largest is null) continue;
            used.Add(largest);
            try
            {
                if (Picture(doc, largest) is { } picture) images.Add(picture with { Page = i + 1 });
                else notDecoded++;
            }
            catch (DocumentLimitException ex)
            {
                // The document's decoding budget is spent: the pages read so far, the rest counted by Pages.
                Diagnostics.Swallow("PdfPageImages", ex);
                break;
            }
        }
        return new(images, notDecoded, pages.Count);
    }

    /// <summary>The image XObjects a page's resources hold, forms one level down included (some scanners wrap the page).</summary>
    private static IEnumerable<PdfStream> Images(PdfDocument doc, Dictionary<string, object?>? resources, int depth)
    {
        if (doc.Dict(resources?.GetValueOrDefault("XObject")) is not { } xobjects) yield break;
        foreach (var value in xobjects.Values)
        {
            if (doc.Resolve(value) is not PdfStream stream) continue;
            var subtype = PdfDocument.Name(stream.Dict.GetValueOrDefault("Subtype"));
            if (subtype == "Image") yield return stream;
            else if (subtype == "Form" && depth < 2)
                foreach (var inner in Images(doc, doc.Dict(stream.Dict.GetValueOrDefault("Resources")), depth + 1)) yield return inner;
        }
    }

    /// <summary>The picture of <paramref name="image"/> as a file's bytes; <c>null</c> for a form not decoded here.</summary>
    private static PdfPageImage? Picture(PdfDocument doc, PdfStream image)
    {
        var filters = doc.Resolve(image.Dict.GetValueOrDefault("Filter")) switch
        {
            PdfName n => [n.Value],
            List<object?> list => list.Select(f => PdfDocument.Name(doc.Resolve(f)) ?? string.Empty).ToList(),
            _ => new List<string>(),
        };
        // A predictor (PNG row filters, img2pdf) or an inverted Decode array would come out sheared or negative: not decoded here.
        var parms = doc.Dict(doc.Resolve(image.Dict.GetValueOrDefault("DecodeParms")) is List<object?> { Count: > 0 } list0 ? list0[^1] : image.Dict.GetValueOrDefault("DecodeParms"));
        if (doc.Num(parms?.GetValueOrDefault("Predictor")) >= 2
            || doc.Resolve(image.Dict.GetValueOrDefault("Decode")) is List<object?> { Count: >= 2 } decode && doc.Num(decode[0]) > doc.Num(decode[1]))
            return null;
        var data = doc.Decode(image);   // stops before the first filter it does not know: a JPEG comes out as the JPEG
        if (filters.Contains("DCTDecode") || filters.Contains("DCT")) return new(0, data, ".jpg");
        if (filters.Any(f => f is "CCITTFaxDecode" or "CCF" or "JBIG2Decode" or "JPXDecode" or "RunLengthDecode" or "RL")) return null;
        // Compressed pixels: 8 bits a component, gray or RGB (directly, or an ICC profile of 1 or 3 components).
        var width = (int)doc.Num(image.Dict.GetValueOrDefault("Width"));
        var height = (int)doc.Num(image.Dict.GetValueOrDefault("Height"));
        var bits = (int)doc.Num(image.Dict.GetValueOrDefault("BitsPerComponent"));
        var components = Components(doc, image.Dict.GetValueOrDefault("ColorSpace"));
        if (width <= 0 || height <= 0 || (long)width * height > ChatImages.MaxPixels || bits != 8 || components is not (1 or 3)
            || data.LongLength < (long)width * height * components) return null;
        var rgba = new byte[width * height * 4];
        for (int p = 0, s = 0; p < width * height; p++, s += components)
        {
            rgba[p * 4] = data[s];
            rgba[p * 4 + 1] = components == 3 ? data[s + 1] : data[s];
            rgba[p * 4 + 2] = components == 3 ? data[s + 2] : data[s];
            rgba[p * 4 + 3] = 255;
        }
        using var output = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output);
        return new(0, output.ToArray(), ".png");
    }

    private static int Components(PdfDocument doc, object? colorSpace) => doc.Resolve(colorSpace) switch
    {
        PdfName { Value: "DeviceGray" or "CalGray" } => 1,
        PdfName { Value: "DeviceRGB" or "CalRGB" } => 3,
        List<object?> { Count: 2 } pair when PdfDocument.Name(doc.Resolve(pair[0])) == "ICCBased" && doc.Resolve(pair[1]) is PdfStream icc
            => (int)doc.Num(icc.Dict.GetValueOrDefault("N")),
        _ => 0,
    };

    private static object? Inherited(PdfDocument doc, Dictionary<string, object?> page, string key)
    {
        // Bounded: a damaged PDF's Parent chain can loop.
        var depth = 0;
        for (var node = page; node is not null && depth++ < 64; node = doc.Dict(node.GetValueOrDefault("Parent")))
            if (node.TryGetValue(key, out var value)) return value;
        return null;
    }

    private static void Collect(PdfDocument doc, object? node, List<Dictionary<string, object?>> pages, HashSet<object> seen, int depth)
    {
        if (depth > 64 || pages.Count > PdfText.MaxPages || doc.Dict(node) is not { } dict || !seen.Add(dict)) return;
        if (PdfDocument.Name(dict.GetValueOrDefault("Type")) == "Page" || !dict.ContainsKey("Kids")) { pages.Add(dict); return; }
        if (doc.Resolve(dict.GetValueOrDefault("Kids")) is List<object?> kids)
            foreach (var kid in kids) Collect(doc, kid, pages, seen, depth + 1);
    }
}
