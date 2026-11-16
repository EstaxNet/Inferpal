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
        var script = Path.Combine(dir, "ocr.ps1");
        // With a byte order mark: Windows PowerShell 5.1 reads a script without one in the ANSI code page.
        File.WriteAllText(script, WindowsScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var output = Path.Combine(dir, "text.txt");
        var run = Run(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                      ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Pdf", path, "-Out", output,
                       "-MaxPages", MaxPages.ToString(CultureInfo.InvariantCulture)], password, ct);
        if (run.Exit == 3)
            return new(OcrStatus.Unavailable, string.Empty, "Windows OCR", 0, 0,
                       "Windows has no OCR language installed: the person can add a language with its text recognition in Windows' language settings");
        return Finish(run, output, "Windows OCR");
    }

    // ── Every system: SimdPaddleOCR (pure C#, no native code) ──

    // The model's own measure of a line, high on the alphabets it reads: the median line of a page of Latin or Chinese text
    // scores 36 or more, a page in an alphabet it does not read (Cyrillic, kana, hangul) 29 or less — read, it would be
    // confident garbage. Measured at pari 96 (docs/probes/pari96/resultats.md).
    private const float ForeignAlphabetScore = 33f;

    /// <summary>The pages' pictures (<see cref="PdfPageImages"/>) read by PP-OCRv6 (tiny), in this process: the same reading on
    /// every system, nothing to install. Latin and Chinese alphabets; a page in another one is said, never read as garbage.</summary>
    internal static OcrReading Paddle(string path, string? password, CancellationToken ct)
    {
        const string engine = "PaddleOCR";
        var until = DateTime.UtcNow + Budget;
        var extracted = PdfPageImages.Extract(File.ReadAllBytes(path), MaxPages, ct, password);
        if (extracted.Images.Count == 0)
            return new(OcrStatus.Unavailable, string.Empty, engine, 0, extracted.Pages,
                       extracted.NotDecoded > 0 ? "its pages are pictures in an encoding Inferpal does not decode (a black-and-white fax, JPEG 2000)"
                                                : "no page picture could be taken from it");
        using var ocr = PaddleOcrAll.LoadAsync(ChineseV6TinyModels.Default).GetAwaiter().GetResult();
        var text = new StringBuilder();
        var skipped = new List<(int Page, string Why)>();
        int read = 0, foreign = 0, lastPage = 0;
        var several = extracted.Pages > 1;
        foreach (var picture in extracted.Images)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > until) break;
            // Pages before this one with no picture the engine could take (none, or one in an encoding not decoded here).
            for (var p = lastPage + 1; p < picture.Page; p++) skipped.Add((p, "no page picture Inferpal decodes"));
            lastPage = picture.Page;
            read++;
            // The size a picture declares, checked before anything decodes it: a JPEG header of a few bytes can ask for gigabytes.
            var (_, width, height) = ChatImages.Sniff(picture.Bytes);
            if (width <= 0 || height <= 0 || (long)width * height > ChatImages.MaxPixels) { skipped.Add((picture.Page, "a page picture too large or unreadable")); continue; }
            PaddleOcrLine[] lines;
            int imageWidth;
            try
            {
                var image = StbImageSharp.ImageResult.FromMemory(picture.Bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                lines = ocr.Run(image.Data, image.Width, image.Height, format: ImagePixelFormat.Rgba32).Lines;
                imageWidth = image.Width;
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
            if (scores[scores.Count / 2] < ForeignAlphabetScore)
            {
                foreign++;
                skipped.Add((picture.Page, lines.Length < 3 ? "no printed text the OCR reads (a signature, a stamp, a picture), or an alphabet it does not read"
                                                            : ForeignAlphabet));
                continue;
            }
            if (text.Length > 0) text.Append("\n\n");
            if (several) text.Append(string.Create(CultureInfo.InvariantCulture, $"--- page {picture.Page} ---\n"));
            text.Append(Rows(lines.Select(OcrLine.Of).ToList(), imageWidth));
        }
        var total = Math.Max(extracted.Pages, extracted.Images.Count);
        var gaps = skipped.Count == 0 ? null
            : string.Join("; ", skipped.GroupBy(s => s.Why).Select(g => $"page{(g.Count() > 1 ? "s" : "")} {string.Join(", ", g.Select(s => s.Page))}: {g.Key}"));
        if (text.Length > 0) return new(OcrStatus.Read, text.ToString(), engine, read, total, Skipped: gaps);
        if (foreign > 0)
            return new(OcrStatus.Unavailable, string.Empty, engine, read, total, skipped.Count == 1 ? skipped[0].Why : ForeignAlphabet,
                       Lasting: read == extracted.Images.Count);
        if (read < extracted.Images.Count) return new(OcrStatus.Failed, string.Empty, engine, read, total, TimedOut);
        return skipped.Count > 0 ? new(OcrStatus.Failed, string.Empty, engine, read, total, gaps)
                                 : new(OcrStatus.Empty, string.Empty, engine, read, total);
    }

    private const string ForeignAlphabet = "its text is in an alphabet this OCR does not read (it reads Latin and Chinese: not Cyrillic, Japanese kana or Korean)";

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
    /// The page's lines in the OCR's order, a table's put back in rows (the rule of the Windows script, see the remarks): short
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

    private sealed record Ran(int Exit, string Stdout, string Stderr, bool TimedOut);

    /// <summary>A process run to its end or to <see cref="Budget"/> (tree killed); the password, when any, by the environment —
    /// never on a command line another process can read.</summary>
    private static Ran Run(string file, IEnumerable<string> args, string? password, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (!string.IsNullOrEmpty(password)) psi.Environment["INFERPAL_PDF_PASSWORD"] = password;
        using var process = ChildProcess.Start(psi);
        // No token on the reads: the pipes end when the process does, killed or not.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var until = DateTime.UtcNow + Budget;
        var timedOut = false;
        while (!process.WaitForExit(100))
        {
            if (!ct.IsCancellationRequested && DateTime.UtcNow <= until) continue;
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            timedOut = true;
            break;
        }
        process.WaitForExit();
        ct.ThrowIfCancellationRequested();
        // The process has exited: both reads are complete, nothing waits.
        return new(timedOut ? -1 : process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), timedOut);
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
    $boxes = @($result.Lines | ForEach-Object {
        $l = $_; $rects = $l.Words | ForEach-Object { $_.BoundingRect }
        $x0 = ($rects | ForEach-Object { $_.X } | Measure-Object -Minimum).Minimum
        $x1 = ($rects | ForEach-Object { $_.X + $_.Width } | Measure-Object -Maximum).Maximum
        [pscustomobject]@{ Text = $l.Text; X = $x0; W = $x1 - $x0
                           Mid = ($rects | ForEach-Object { $_.Y + $_.Height / 2 } | Measure-Object -Average).Average
                           H = ($rects | ForEach-Object { $_.Height } | Measure-Object -Average).Average } })
    # Rows of short lines; a run of rows is a table when it holds 3 rows of 2 cells or more, and a line outside it between
    # two rows ends the run (a header row far above the items, two address blocks side by side, are no table).
    $rowOf = @{}
    $rows = New-Object System.Collections.ArrayList
    foreach ($bx in ($boxes | Where-Object { $_.W -lt 0.35 * $pixelWidth } | Sort-Object Mid)) {
        $row = $rows | Where-Object { [Math]::Abs($_.Mid - $bx.Mid) -lt $bx.H / 2 } | Select-Object -First 1
        if ($null -eq $row) { $row = [pscustomobject]@{ Mid = $bx.Mid; Cells = New-Object System.Collections.ArrayList }; [void]$rows.Add($row) }
        [void]$row.Cells.Add($bx); $rowOf[$bx] = $row
    }
    $sorted = @($rows | Where-Object { $_.Cells.Count -ge 2 } | Sort-Object Mid)
    $runs = New-Object System.Collections.ArrayList
    $current = New-Object System.Collections.ArrayList
    foreach ($row in $sorted) {
        if ($current.Count -gt 0) {
            $low = $current[$current.Count - 1].Mid; $high = $row.Mid
            $between = @($boxes | Where-Object { $_.Mid -gt $low + $_.H / 2 -and $_.Mid -lt $high - $_.H / 2 -and -not ($rowOf.ContainsKey($_) -and $rowOf[$_].Cells.Count -ge 2) })
            if ($between.Count -gt 0) { [void]$runs.Add($current); $current = New-Object System.Collections.ArrayList }
        }
        [void]$current.Add($row)
    }
    if ($current.Count -gt 0) { [void]$runs.Add($current) }
    $tableOf = @{}
    foreach ($run in $runs) { if ($run.Count -ge 3) { foreach ($row in $run) { foreach ($c in $row.Cells) { $tableOf[$c] = $run } } } }
    $done = @{}
    $text = New-Object System.Text.StringBuilder
    if ($i -gt 0) { [void]$text.AppendLine() }
    foreach ($bx in $boxes) {
        if (-not $tableOf.ContainsKey($bx)) { [void]$text.AppendLine($bx.Text); continue }
        $run = $tableOf[$bx]
        if ($done.ContainsKey($run)) { continue }
        foreach ($row in $run) { [void]$text.AppendLine((($row.Cells | Sort-Object X | ForEach-Object { $_.Text }) -join ' | ')) }
        $done[$run] = $true
    }
    [IO.File]::AppendAllText($Out, $text.ToString(), $utf8)
    "PAGE $($i + 1)"
}
""";
}
