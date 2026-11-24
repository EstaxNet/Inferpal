using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;

namespace Inferpal.Services.Documents;

/// <summary>How an OCR reading of a scan ended.</summary>
internal enum OcrStatus
{
    /// <summary>Text was read.</summary>
    Read,
    /// <summary>The OCR ran and found no text (a photo, a blank page): a fact about the file, kept.</summary>
    Empty,
    /// <summary>No OCR here reads it (an alphabet the engine does not read, a page picture it cannot decode):
    /// <see cref="OcrReading.Why"/> says why. Not kept.</summary>
    Unavailable,
    /// <summary>The reading failed or ran out of time: tried again next time, never kept as the file's answer.</summary>
    Failed,
}

/// <summary>What the OCR read of a scanned PDF.</summary>
/// <param name="Engine">The engine and its language, as the model is told it ("Windows OCR, fr-FR").</param>
/// <param name="Why">For <see cref="OcrStatus.Unavailable"/> and <see cref="OcrStatus.Failed"/>: why, and the gesture.</param>
/// <param name="Skipped">Pages within those read that gave no text to the model, and why ("3, 4: an alphabet …"); null when none.</param>
/// <param name="Lasting">True when the answer holds until the file changes (an alphabet the engine does not read): kept like a
/// reading, not tried again on every read.</param>
internal sealed record OcrReading(OcrStatus Status, string Text, string Engine, int PagesRead, int Pages, string? Why = null,
                                  string? Skipped = null, bool Lasting = false);

/// <summary>
/// A scanned PDF read by OCR on every system, nothing downloaded, nothing installed: on Windows by Windows' own OCR first
/// (<c>Windows.Data.Pdf</c> + <c>Windows.Media.Ocr</c>, through Windows PowerShell 5.1: the Core targets no Windows SDK; every
/// language installed), and everywhere else — or when Windows has no OCR language — by PP-OCRv6 in this process
/// (<c>Sdcb.SimdPaddleOCR</c>, pure C#: Latin and Chinese alphabets, another one said). Measured at pari 96 on ten simulated
/// scans: Windows 99.6 % of the words, PP-OCRv6 tiny 98.7 %.
/// </summary>
/// <remarks>
/// ⚠ An OCR reads a table column by column (every item, then every price): the short lines that share a row are put back in
/// rows, left to right, " | " between cells — only when they make a table (3 rows of 2 cells or more, contiguous: a line
/// between two rows ends it); every other line keeps the OCR's own order, which holds for prose and columns.
/// </remarks>
internal static class PdfOcr
{
    /// <summary>Stands in for the machine's OCR in a test (this flow only: a test elsewhere reads with the real one).</summary>
    internal static readonly AsyncLocal<Func<string, OcrReading>?> ForTests = new();

    /// <summary>Pages read at most; the rest said.</summary>
    internal const int MaxPages = 20;
    /// <summary>The whole reading's budget, after which the process tree is killed — what was read is kept.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);
    private static readonly string TimedOut = string.Create(CultureInfo.InvariantCulture, $"the reading took longer than {Budget.TotalSeconds:N0} s");

    /// <summary>The text of the scanned PDF at <paramref name="path"/>. ⚠ Synchronous: called from a worker
    /// (<c>Task.Run</c>), never on an editor's thread.</summary>
    internal static OcrReading Read(string path, string? password, CancellationToken ct)
    {
        if (ForTests.Value is { } instead) return instead(path);
        var dir = Directory.CreateTempSubdirectory("inferpal-ocr-").FullName;
        try
        {
            if (OperatingSystem.IsWindows() && Windows(path, password, dir, ct) is var windows
                && (windows.Status is OcrStatus.Read or OcrStatus.Empty || windows.Why == TimedOut))
                return windows;
            return Paddle(path, password, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or InvalidOperationException or FormatException or DocumentLimitException)
        {
            Diagnostics.Swallow("PdfOcr", ex);
            return new(OcrStatus.Failed, string.Empty, string.Empty, 0, 0, Diagnostics.RootMessage(ex));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temporary folder: best-effort cleanup */ }
        }
    }

    // ── Windows ──

    private static OcrReading Windows(string path, string? password, string dir, CancellationToken ct)
    {
        var output = Path.Combine(dir, "text.txt");
        var run = Run(ScriptProcess.WindowsPowerShell(dir, "ocr.ps1", WindowsScript, "-Pdf", path, "-Out", output,
                                                      "-MaxPages", MaxPages.ToString(CultureInfo.InvariantCulture)), password, ct);
        if (run.Exit == 3)
            return new(OcrStatus.Unavailable, string.Empty, "Windows OCR", 0, 0,
                       "Windows has no OCR language installed: the person can add a language with its text recognition in Windows' language settings");
        if (File.Exists(output)) File.WriteAllText(output, WindowsPages(File.ReadAllLines(output, Encoding.UTF8)), Encoding.UTF8);
        return Finish(run, output, "Windows OCR");
    }

    /// <summary>The Windows script's pages — one JSON line each, the boxes of its lines — as text, a table's in rows by
    /// <see cref="Rows"/>: one reader for both engines, the page's skew taken out (an OCR that does not straighten its boxes,
    /// as Windows Server's, scattered a table's columns).</summary>
    internal static string WindowsPages(IEnumerable<string> pages)
    {
        var text = new StringBuilder();
        foreach (var page in pages.Where(p => p.TrimStart().StartsWith('{')))
        {
            // A run stopped at the budget may leave its last page half written: the pages before it are kept.
            System.Text.Json.JsonDocument json;
            try { json = System.Text.Json.JsonDocument.Parse(page); }
            catch (System.Text.Json.JsonException) { continue; }
            using var _ = json;
            var width = (float)json.RootElement.GetProperty("width").GetDouble();
            var lines = new List<OcrLine>();
            if (json.RootElement.TryGetProperty("lines", out var array) && array.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var l in array.EnumerateArray())
                    lines.Add(new OcrLine(l.GetProperty("t").GetString() ?? string.Empty, (float)l.GetProperty("x").GetDouble(),
                                          (float)l.GetProperty("w").GetDouble(), (float)l.GetProperty("m").GetDouble(),
                                          (float)l.GetProperty("h").GetDouble(), (float)l.GetProperty("s").GetDouble()));
            if (text.Length > 0) text.Append('\n');
            text.Append(Rows(lines, width));
        }
        return text.ToString();
    }

    // ── Every system: SimdPaddleOCR (pure C#, no native code) ──

    // The model's own measure of a line (the median line of a page): a 300 dpi scan in Latin or Chinese scores 36 or more, a
    // screenshot's small text 30.6 to 36.8, a page in an alphabet it does not read 20.5 to 28.9 (Cyrillic, kana, hangul,
    // Greek) — read, that page would be confident garbage (pari 96 and 108). Under ForeignAlphabetScore a page is doubtful:
    // the other alphabets' recognizers are tried; when none claims it, it is kept above ForeignFloor and said under it.
    // ⚠ The margin is thin (28.9 against 30.6): measured on fifteen pages and ten screenshots, held by a test each way.
    private const float ForeignAlphabetScore = 33f;
    private const float ForeignFloor = 29.75f;

    // A dedicated recognizer's own measure, a probability: the median line of a page in its alphabet scores 0.9 to 1.0, a page
    // in another 0.8 or less (pari 96, the product's ten languages).
    private const float ScriptScore = 0.85f;

    /// <summary>The recognizers of the alphabets the tiny model does not read — the product's other languages — with the
    /// detector and classifier it shares (third-party/paddleocr-models, embedded in this assembly).</summary>
    internal sealed class Scripts
    {
        private static readonly (string Name, string File, Func<char, bool> Own)[] Known =
        [
            ("Cyrillic", "eslav", c => c is >= '\u0400' and <= '\u04FF'),
            ("Japanese", "japan", c => c is >= '\u3040' and <= '\u30FF' or >= '\u4E00' and <= '\u9FFF'),
            ("Korean", "korean", c => c is >= '\uAC00' and <= '\uD7AF' or >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F'),
        ];
        private readonly List<(string Name, PaddleOcrAll Engine, Func<char, bool> Own)> _engines = [];

        public Scripts(PaddleOcrModelSet shared)
        {
            // Width fixed as the models were converted (1×3×48×960); the orientation classifier off: it turned lines over.
            var recognizer = new PaddleOcrRecognizerOptions();
            Set(recognizer, nameof(PaddleOcrRecognizerOptions.AdaptiveWidth), false);
            Set(recognizer, nameof(PaddleOcrRecognizerOptions.TargetWidth), 960);
            var options = new PaddleOcrOptions();
            Set(options, nameof(PaddleOcrOptions.UseDirectionClassification), false);
            // One line a batch: the models were converted with a fixed input of one line (1×3×48×960).
            Set(options, nameof(PaddleOcrOptions.RecBatchLines), 1);
            Set(options, nameof(PaddleOcrOptions.Recognizer), recognizer);
            foreach (var (name, file, own) in Known)
                _engines.Add((name, new PaddleOcrAll(shared.DetectionModel, shared.ClassificationModel,
                                                     Sdcb.SimdPaddleOCR.OnnxSharp.Model.LoadOnnx(Resource(file + ".onnx")),
                                                     Resource(file + ".txt"), options), own));
        }

        /// <summary>
        /// Sets an option by reflection. ⚠ The library ships one build per framework: this assembly compiles against its .NET
        /// Standard build, where the options have setters, and runs on .NET 10 (the VS Code and ACP host, Visual Studio's) with
        /// its .NET 10 build, where they are init-only — a compiled setter call is then a MissingMethodException.
        /// <see cref="System.Reflection.PropertyInfo.SetValue(object, object)"/> sets both.
        /// </summary>
        private static void Set(object options, string property, object value) =>
            (options.GetType().GetProperty(property) ?? throw new InvalidOperationException($"No option {property} in this OCR build."))
                .SetValue(options, value);

        private static byte[] Resource(string name)
        {
            using var stream = typeof(PdfOcr).Assembly.GetManifestResourceStream("Inferpal.Ocr." + name)
                               ?? throw new InvalidOperationException($"The OCR resource {name} is not in this build.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }

        /// <summary>
        /// The page read by the recognizer most sure of it, when one is sure enough AND what it read is in its own alphabet;
        /// <c>null</c> otherwise. ⚠ The Cyrillic and Korean recognizers also read English: a Latin page the tiny model read
        /// poorly would come back "sure" in Latin letters with its accents lost — such a page stays said, never rescued.
        /// </summary>
        public (string Name, PaddleOcrLine[] Lines)? Read(StbImageSharp.ImageResult image)
        {
            (string Name, PaddleOcrLine[] Lines, float Median)? best = null;
            foreach (var (name, engine, own) in _engines)
            {
                var lines = engine.Run(image.Data, image.Width, image.Height, format: ImagePixelFormat.Rgba32).Lines;
                if (lines.Length == 0) continue;
                var letters = lines.SelectMany(l => l.Text).Where(char.IsLetter).ToList();
                if (letters.Count == 0 || letters.Count(own) < letters.Count * 0.3) continue;   // not its alphabet
                var median = lines.Select(l => l.RecognitionScore).Order().ElementAt(lines.Length / 2);
                if (best is null || median > best.Value.Median) best = (name, lines, median);
            }
            return best is { Median: >= ScriptScore } sure ? (sure.Name, sure.Lines) : null;
        }
    }

    /// <summary>The pages' pictures (<see cref="PdfPageImages"/>) read by PP-OCRv6 (tiny), in this process: the same reading on
    /// every system, nothing to install. Latin and Chinese alphabets; a page in another is read again by the recognizers of the
    /// product's other alphabets (Cyrillic, Japanese, Korean), the one most sure of it kept; one none reads is said, never read
    /// as garbage.</summary>
    internal static OcrReading Paddle(string path, string? password, CancellationToken ct)
    {
        var extracted = PdfPageImages.Extract(File.ReadAllBytes(path), MaxPages, ct, password);
        if (extracted.Images.Count == 0)
            return new(OcrStatus.Unavailable, string.Empty, "PaddleOCR", 0, extracted.Pages,
                       extracted.NotDecoded > 0 ? "its pages are pictures in an encoding Inferpal does not decode (a black-and-white fax, JPEG 2000)"
                                                : "no page picture could be taken from it");
        return Pictures(extracted.Images, extracted.Pages, ct);
    }

    /// <summary>
    /// The text of one picture — a screenshot, a photo of a page — by the same engine as a scan's pages (every system, the
    /// product's ten languages). <paramref name="bytes"/> are the file's (PNG, JPEG, BMP, GIF).
    /// </summary>
    internal static OcrReading Picture(byte[] bytes, CancellationToken ct)
    {
        if (ForTests.Value is { } instead) return instead("picture");
        try { return Pictures([new PdfPageImage(1, bytes, "")], 1, ct); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException)
        {
            Diagnostics.Swallow("PdfOcr.Picture", ex);
            return new(OcrStatus.Failed, string.Empty, "PaddleOCR", 0, 1, Diagnostics.RootMessage(ex));
        }
    }

    /// <summary>The pictures read in order, the pages left out named (the loop both a scan and a picture go through).</summary>
    private static OcrReading Pictures(IReadOnlyList<PdfPageImage> pictures, int pages, CancellationToken ct)
    {
        var engine = "PaddleOCR";
        var until = DateTime.UtcNow + Budget;
        // ⚠ The set outlives every engine built on it: the recognizers of other alphabets share its detector and classifier.
        using var models = PaddleOcrModelSet.LoadAsync(ChineseV6TinyModels.Default, ct).GetAwaiter().GetResult();
        // Owns the set (this overload), never disposed itself: the set's `using` releases the models once, after every engine.
        var ocr = new PaddleOcrAll(models, new PaddleOcrOptions());
        Scripts? scripts = null;
        string? scriptsFailed = null;
        var alphabets = new SortedSet<string>(StringComparer.Ordinal);
        var text = new StringBuilder();
        var skipped = new List<(int Page, string Why)>();
        int read = 0, foreign = 0, lastPage = 0;
        var several = pages > 1;
        foreach (var picture in pictures)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > until) break;
            // Pages before this one with no picture the engine could take (none, or one in an encoding not decoded here).
            for (var p = lastPage + 1; p < picture.Page; p++) skipped.Add((p, "no page picture Inferpal decodes"));
            lastPage = picture.Page;
            read++;
            // The size a picture declares, checked before anything decodes it: a JPEG header of a few bytes can ask for gigabytes.
            var (_, width, height) = ChatImages.Sniff(picture.Bytes);
            if (width <= 0 && picture.Bytes is [(byte)'B', (byte)'M', ..] && picture.Bytes.Length >= 26)
                (width, height) = (BitConverter.ToInt32(picture.Bytes, 18), Math.Abs(BitConverter.ToInt32(picture.Bytes, 22)));
            if (width <= 0 || height <= 0 || (long)width * height > ChatImages.MaxPixels) { skipped.Add((picture.Page, "a page picture too large or unreadable")); continue; }
            PaddleOcrLine[] lines;
            int imageWidth;
            var otherAlphabet = false;
            try
            {
                var image = StbImageSharp.ImageResult.FromMemory(picture.Bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                lines = ocr.Run(image.Data, image.Width, image.Height, format: ImagePixelFormat.Rgba32).Lines;
                imageWidth = image.Width;
                // Another alphabet: read again by the recognizer of the product's other alphabets most sure of the page — a page
                // of three lines or more (a stamp's or a signature's line or two stays said, never rescued into invented text).
                if (lines.Length >= 3 && lines.Select(l => l.RecognitionScore).Order().ElementAt(lines.Length / 2) < ForeignAlphabetScore
                    && LoadScripts() is { } loaded && loaded.Read(image) is { } other)
                {
                    lines = other.Lines;
                    alphabets.Add(other.Name);
                    otherAlphabet = true;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                // One damaged page: said, the others still read.
                Diagnostics.Swallow("PdfOcr.Paddle", ex);
                skipped.Add((picture.Page, "a damaged page picture"));
                continue;
            }
            if (lines.Length == 0) continue;
            var scores = lines.Select(l => l.RecognitionScore).Order().ToList();
            // Read by another alphabet's recognizer (its scores are probabilities, under this one's scale): kept.
            if (!otherAlphabet && scores[scores.Count / 2] < ForeignFloor)
            {
                foreign++;
                skipped.Add((picture.Page, lines.Length < 3 ? "no printed text the OCR reads (a signature, a stamp, a picture), or an alphabet it does not read"
                                                            : ForeignAlphabet));
                continue;
            }
            if (text.Length > 0) text.Append("\n\n");
            if (several) text.Append(string.Create(CultureInfo.InvariantCulture, $"--- page {picture.Page} ---\n"));
            // Between the floor and the doubt: kept (a screenshot's small text), and said — an alphabet nobody measured
            // (Arabic, Hebrew, Thai) could land here as confident garbage.
            if (!otherAlphabet && scores[scores.Count / 2] < ForeignAlphabetScore)
                text.Append("[Read with low confidence: small text, or an alphabet this OCR does not read — check it against the image.]\n");
            text.Append(ErrorCodes(Rows(lines.Select(OcrLine.Of).ToList(), imageWidth)));
        }
        var total = Math.Max(pages, pictures.Count);
        if (scriptsFailed is not null)
            for (var i = 0; i < skipped.Count; i++)
                if (skipped[i].Why == ForeignAlphabet)
                    skipped[i] = (skipped[i].Page, $"{ForeignAlphabet[..^1]}; the recognizers of the other alphabets could not load: {scriptsFailed})");
        var gaps = skipped.Count == 0 ? null
            : string.Join("; ", skipped.GroupBy(s => s.Why).Select(g => $"page{(g.Count() > 1 ? "s" : "")} {string.Join(", ", g.Select(s => s.Page))}: {g.Key}"));
        if (alphabets.Count > 0) engine += " with its " + string.Join(", ", alphabets) + " recognizer" + (alphabets.Count > 1 ? "s" : "");
        if (text.Length > 0) return new(OcrStatus.Read, text.ToString(), engine, read, total, Skipped: gaps);
        if (foreign > 0)
            return new(OcrStatus.Unavailable, string.Empty, engine, read, total, skipped.Count == 1 ? skipped[0].Why : ForeignAlphabet,
                       Lasting: read == pictures.Count && scriptsFailed is null);

        // Loaded once, on the first page in another alphabet; a failure said with its cause, never tried again in this reading.
        Scripts? LoadScripts()
        {
            if (scripts is not null || scriptsFailed is not null) return scripts;
            try { return scripts = new Scripts(models); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException
                                           or NotSupportedException or OverflowException or IndexOutOfRangeException)
            {
                Diagnostics.Swallow("PdfOcr.Scripts", ex);
                scriptsFailed = Diagnostics.RootMessage(ex);
                return null;
            }
        }
        if (read < pictures.Count) return new(OcrStatus.Failed, string.Empty, engine, read, total, TimedOut);
        return skipped.Count > 0 ? new(OcrStatus.Failed, string.Empty, engine, read, total, gaps)
                                 : new(OcrStatus.Empty, string.Empty, engine, read, total);
    }

    private const string ForeignAlphabet = "its text is in an alphabet this OCR does not read (it reads Latin, Chinese, Cyrillic, Japanese and Korean)";

    // The prefixes of the error codes a developer searches for, followed by their four or five digits, their first letter read
    // upright: the OCR reads "Cs0246" for CS0246. A lowercase start (a git hash "ca12345", an identifier "ts2023") is left.
    private static readonly System.Text.RegularExpressions.Regex ErrorCode = new(
        @"\b(C[sS]|M[sS][bB]|N[uU]|N[eE][tT][sS][dD][kK]|T[sS]|C[aA]|I[dD][eE]|B[cC]|F[sS])(\d{4,5})\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        RegexBudget.Default);

    /// <summary><paramref name="text"/> with the error codes it holds in their own case (<c>CS0246</c>, <c>MSB4019</c>).</summary>
    internal static string ErrorCodes(string text) =>
        ErrorCode.Replace(text, m => m.Groups[1].Value.ToUpperInvariant() + m.Groups[2].Value);

    /// <summary>A line the OCR read: its text, left edge and width, middle and height, and the slope of its top edge.</summary>
    internal readonly record struct OcrLine(string Text, float X, float Width, float Middle, float Height, float Slope)
    {
        internal static OcrLine Of(PaddleOcrLine line)
        {
            var b = line.Box;
            float left = Math.Min(Math.Min(b.X1, b.X2), Math.Min(b.X3, b.X4)), right = Math.Max(Math.Max(b.X1, b.X2), Math.Max(b.X3, b.X4));
            return new(line.Text, left, right - left, (b.Y1 + b.Y2 + b.Y3 + b.Y4) / 4,
                       Math.Max(Math.Abs(b.Y4 - b.Y1), Math.Abs(b.Y3 - b.Y2)), b.X2 != b.X1 ? (b.Y2 - b.Y1) / (b.X2 - b.X1) : 0f);
        }
    }

    /// <summary>
    /// The page's lines in the OCR's order, a table's put back in rows (both engines' one reader: Paddle's lines and the Windows
    /// script's boxes, <see cref="WindowsPages"/>): short
    /// lines (under 35 % of the page) that share a row once the page's skew is taken out — read on its long lines only, the
    /// detector draws short ones flat — joined " | " left to right, when 3 rows or more of 2 cells or more follow each other
    /// with no other line between them.
    /// </summary>
    internal static string Rows(IReadOnlyList<OcrLine> lines, float pageWidth)
    {
        var slopes = lines.Where(l => l.Width > 0.4f * pageWidth).Select(l => l.Slope).Order().ToList();
        var skew = slopes.Count > 0 ? slopes[slopes.Count / 2] : 0f;
        var mid = lines.Select(l => l.Middle - skew * (l.X + l.Width / 2)).ToList();
        var rows = new List<(float Mid, List<int> Cells)>();
        var rowOf = new Dictionary<int, int>();
        foreach (var i in Enumerable.Range(0, lines.Count).Where(i => lines[i].Width < 0.35f * pageWidth).OrderBy(i => mid[i]))
        {
            var r = rows.FindIndex(x => Math.Abs(x.Mid - mid[i]) < lines[i].Height / 2);
            if (r < 0) { rows.Add((mid[i], [])); r = rows.Count - 1; }
            rows[r].Cells.Add(i);
            rowOf[i] = r;
        }
        bool InATableRow(int i) => rowOf.TryGetValue(i, out var r) && rows[r].Cells.Count >= 2;
        var runs = new List<List<int>>();
        var current = new List<int>();
        foreach (var r in Enumerable.Range(0, rows.Count).Where(r => rows[r].Cells.Count >= 2).OrderBy(r => rows[r].Mid))
        {
            if (current.Count > 0)
            {
                float low = rows[current[^1]].Mid, high = rows[r].Mid;
                if (Enumerable.Range(0, lines.Count).Any(i => mid[i] > low + lines[i].Height / 2 && mid[i] < high - lines[i].Height / 2 && !InATableRow(i)))
                    (runs, current) = ([.. runs, current], []);
            }
            current.Add(r);
        }
        if (current.Count > 0) runs.Add(current);
        var tableOf = new Dictionary<int, List<int>>();
        foreach (var run in runs.Where(run => run.Count >= 3))
            foreach (var r in run)
                foreach (var cell in rows[r].Cells) tableOf[cell] = run;
        var done = new HashSet<List<int>>();
        var text = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!tableOf.TryGetValue(i, out var run)) { text.Add(lines[i].Text); continue; }
            if (!done.Add(run)) continue;
            foreach (var r in run) text.Add(string.Join(" | ", rows[r].Cells.OrderBy(c => lines[c].X).Select(c => lines[c].Text)));
        }
        return string.Join("\n", text);
    }

    // ── Shared ──

    internal sealed record Ran(int Exit, string Stdout, string Stderr, bool TimedOut);

    /// <summary>A process run to its end or to <see cref="Budget"/> (tree killed); the password, when any, by the environment —
    /// never on a command line another process can read.</summary>
    /// <remarks>⚠ Through <see cref="ChildProcess.RunAsync"/>: a child the script leaves behind holding its output is not
    /// waited for. Waited for on the reader's own thread (no synchronization context), as the binlog replay is.</remarks>
    internal static Ran Run(ProcessStartInfo psi, string? password, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(password)) psi.Environment["INFERPAL_PDF_PASSWORD"] = password;
        var run = ChildProcess.RunAsync(psi, Budget, ct).GetAwaiter().GetResult();
        return new(run.ExitCode, run.Stdout, run.Stderr, run.TimedOut);
    }

    /// <summary>A script's reading: the text it wrote page by page (kept when it was stopped), its LANG and PAGE lines.</summary>
    private static OcrReading Finish(Ran run, string output, string engine)
    {
        var lang = Line(run.Stdout, "LANG ");
        var label = lang is null or "auto" ? engine : $"{engine}, {lang}";
        var pagesLine = Line(run.Stdout, "PAGES ")?.Split('/');
        var total = pagesLine is [_, var t] && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        var read = run.Stdout.Split('\n').Count(l => l.StartsWith("PAGE ", StringComparison.Ordinal));
        var text = File.Exists(output) ? File.ReadAllText(output, Encoding.UTF8).Trim() : string.Empty;
        if (text.Length > 0) return new(OcrStatus.Read, text, label, read, Math.Max(total, read));
        if (run.TimedOut || run.Exit != 0)
        {
            Diagnostics.Record("PdfOcr", $"{engine}: exit {run.Exit}{(run.TimedOut ? " (stopped at the budget)" : "")}: {run.Stderr.Trim()}");
            return new(OcrStatus.Failed, string.Empty, label, read, total,
                       run.TimedOut ? TimedOut : "the OCR failed (see /diagnostics)");
        }
        return new(OcrStatus.Empty, string.Empty, label, read, total);
    }

    private static string? Line(string output, string prefix) =>
        output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    /// <summary>Windows: the OCR in Windows PowerShell 5.1 (the probe's script, docs/probes/pari96/ocr.ps1, made a product's).
    /// Each page's text is appended as it is read and its PAGE line printed: a run stopped at the budget keeps them.</summary>
    private const string WindowsScript = """
param([string]$Pdf, [string]$Out, [int]$MaxPages = 20, [int]$Width = 2480)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$asTaskOp = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
$asTaskAction = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' })[0]
function Await($op, [Type]$type) { $t = $asTaskOp.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(); $t.Result }
function AwaitAction($op) { $t = $asTaskAction.Invoke($null, @($op)); $t.Wait() }
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if ($null -eq $engine) {
    $first = [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | Select-Object -First 1
    if ($null -ne $first) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($first) }
}
if ($null -eq $engine) { [Console]::Error.WriteLine('no OCR language installed'); exit 3 }
"LANG $($engine.RecognizerLanguage.LanguageTag)"
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Pdf)) ([Windows.Storage.StorageFile])
$doc = if ($env:INFERPAL_PDF_PASSWORD) { Await ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file, $env:INFERPAL_PDF_PASSWORD)) ([Windows.Data.Pdf.PdfDocument]) }
       else { Await ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument]) }
$count = [Math]::Min([int]$doc.PageCount, $MaxPages)
"PAGES 0/$($doc.PageCount)"
$utf8 = [Text.UTF8Encoding]::new($false)
for ($i = 0; $i -lt $count; $i++) {
    $page = $doc.GetPage($i)
    $stream = [Windows.Storage.Streams.InMemoryRandomAccessStream]::new()
    $options = [Windows.Data.Pdf.PdfPageRenderOptions]::new()
    $options.DestinationWidth = $Width
    AwaitAction ($page.RenderToStreamAsync($stream, $options))
    $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    $pixelWidth = $bitmap.PixelWidth
    $bitmap.Dispose(); $stream.Dispose(); $page.Dispose()
    # Each line's box and slope, one JSON line per page: the rows are rebuilt by the product's one reader (Rows, in C#), which
    # takes the page's skew out first.
    $boxes = @($result.Lines | ForEach-Object {
        $words = @($_.Words | Sort-Object { $_.BoundingRect.X })
        $rects = $words | ForEach-Object { $_.BoundingRect }
        $x0 = ($rects | ForEach-Object { $_.X } | Measure-Object -Minimum).Minimum
        $x1 = ($rects | ForEach-Object { $_.X + $_.Width } | Measure-Object -Maximum).Maximum
        $first = $words[0].BoundingRect; $last = $words[$words.Count - 1].BoundingRect
        $run = ($last.X + $last.Width / 2) - ($first.X + $first.Width / 2)
        $slope = if ($words.Count -ge 2 -and $run -gt 0) { (($last.Y + $last.Height / 2) - ($first.Y + $first.Height / 2)) / $run } else { 0 }
        [ordered]@{ t = $_.Text; x = $x0; w = $x1 - $x0
                    m = ($rects | ForEach-Object { $_.Y + $_.Height / 2 } | Measure-Object -Average).Average
                    h = ($rects | ForEach-Object { $_.Height } | Measure-Object -Average).Average; s = $slope } })
    $text = (ConvertTo-Json -Compress -Depth 4 -InputObject ([ordered]@{ width = $pixelWidth; lines = $boxes })) + "`n"
    [IO.File]::AppendAllText($Out, $text.ToString(), $utf8)
    "PAGE $($i + 1)"
}
""";
}
