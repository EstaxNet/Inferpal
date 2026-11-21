using System.ComponentModel;
using System.Globalization;
using System.Text;
using Inferpal.Localization;
using StbImageResizeSharp;
using StbImageSharp;
using StbImageWriteSharp;

namespace Inferpal.Services.Documents;

/// <summary>
/// A screen recording of a bug read as pictures the model can see: <see cref="Count"/> frames at even times, laid out on
/// one contact sheet, each with its time written on it, so the model says WHEN ("at 0:30"). Decoded by the system it runs
/// on — Windows' own media stack (WinRT), ffmpeg on macOS and Linux — and a GIF that moves by Inferpal itself.
/// </summary>
/// <remarks>⚠ The decoder may be missing (a .webm on Windows without the VP9 extension, ffmpeg absent): said with what installs
/// it, never a silent refusal — as HEIC (<see cref="HeicImage"/>). ⚠ One picture, not eight: the chip, the model's eyes,
/// the text read for a model that does not see, the saved conversation, every path an image takes stays one image.
/// ⚠ A video or a GIF is the person's or the workspace's file, so untrusted: its pixels, frames and dimensions are bounded
/// before anything is allocated, and nothing here throws out of an attachment.</remarks>
internal static class VideoFrames
{
    /// <summary>The videos read this way. A GIF is an image (<see cref="ChatImages"/>) unless it moves.</summary>
    internal static readonly string[] Extensions = [".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".wmv"];

    /// <summary>The largest video read: a minute of screen recording is tens of megabytes.</summary>
    internal const long MaxBytes = 100L * 1024 * 1024;

    /// <summary>How many frames the sheet holds.</summary>
    internal const int Count = 8;

    /// <summary>How long the decoding may take, all calls together.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    /// <summary>The width of one frame on the sheet: four to a row and their gaps are 1916 px, under what an image is scaled
    /// to (1920).</summary>
    private const int CellWidth = 476;

    /// <summary>The tallest a frame may be on the sheet: a frame of an extreme shape is fitted, never drawn its full height.</summary>
    private const int CellMaxHeight = CellWidth * 2;

    internal static bool Handles(string name) => Extensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    /// <summary>One frame: when it is, and its picture (PNG or JPEG bytes, or RGBA when <see cref="Rgba"/>).</summary>
    internal sealed record Frame(TimeSpan At, byte[] Image, int Width = 0, int Height = 0, bool Rgba = false);

    /// <summary>The contact sheet of the video in <paramref name="bytes"/>, and the line its chip shows; or why it could not
    /// be read (in English: the cause and what fixes it). Never throws but for the person's cancel.</summary>
    internal static async Task<(byte[]? Sheet, string? Detail, string? Why)> SheetAsync(byte[] bytes, string name, CancellationToken ct)
    {
        try
        {
            var (frames, duration, why) = await ExtractAsync(bytes, Path.GetExtension(name).ToLowerInvariant(), ct).ConfigureAwait(false);
            if (frames is not { Count: > 0 }) return (null, null, why ?? "no frame could be read from it");
            return Compose(frames) is { } sheet
                ? (sheet.Image, Strings.VideoChip(Time(duration), sheet.Frames), null)
                : (null, null, "none of its frames could be decoded");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Diagnostics.Swallow("VideoFrames.Sheet", ex);
            return (null, null, "it could not be read: " + ex.Message);
        }
    }

    // ── A GIF that moves, read here ──

    /// <summary>A GIF's structure, walked block by block without decoding a pixel: its canvas, and each frame's delay.</summary>
    internal sealed record GifLayout(int Width, int Height, IReadOnlyList<TimeSpan> Delays)
    {
        /// <summary>Whether it moves: more than one frame.</summary>
        public bool Moves => Delays.Count > 1;
    }

    /// <summary>The structure of the GIF in <paramref name="bytes"/>; <c>null</c> when it is not one or is damaged.</summary>
    /// <remarks>By its blocks (extension, image descriptor, trailer), never by searching bytes: a palette or compressed data
    /// can hold any pair of bytes.</remarks>
    internal static GifLayout? Layout(byte[] bytes)
    {
        if (bytes.Length < 13 || bytes[0] != (byte)'G' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F') return null;
        int width = bytes[6] | bytes[7] << 8, height = bytes[8] | bytes[9] << 8;
        var at = 13;
        if ((bytes[10] & 0x80) != 0) at += 3 << ((bytes[10] & 7) + 1);   // the global colour table
        var delays = new List<TimeSpan>();
        var pending = TimeSpan.Zero;
        bool SkipSubBlocks()
        {
            while (at < bytes.Length)
            {
                var size = bytes[at++];
                if (size == 0) return true;
                at += size;
            }
            return false;
        }
        while (at < bytes.Length)
        {
            var block = bytes[at++];
            if (block == 0x3B) break;                                       // trailer
            if (block == 0x21)                                              // an extension
            {
                if (at >= bytes.Length) return null;
                var label = bytes[at++];
                if (label == 0xF9 && at + 4 < bytes.Length && bytes[at] == 4)
                {
                    // A delay of 0 or 10 ms is played at 100 ms by every browser and viewer: the time the person sees.
                    var centis = bytes[at + 2] | bytes[at + 3] << 8;
                    pending = TimeSpan.FromMilliseconds(centis <= 1 ? 100 : centis * 10);
                }
                if (!SkipSubBlocks()) return null;
            }
            else if (block == 0x2C)                                         // an image
            {
                if (at + 9 > bytes.Length) return null;
                var packed = bytes[at + 8];
                at += 9;
                if ((packed & 0x80) != 0) at += 3 << ((packed & 7) + 1);    // its local colour table
                at++;                                                       // the LZW minimum code size
                if (!SkipSubBlocks()) return null;
                delays.Add(pending == TimeSpan.Zero ? TimeSpan.FromMilliseconds(100) : pending);
                pending = TimeSpan.Zero;
            }
            else break;                                                     // anything else ends what can be read
        }
        return delays.Count > 0 ? new(width, height, delays) : null;
    }

    /// <summary>The frames of a GIF that moves, at even times by its own delays, decoded here: only the ones kept are copied.
    /// <c>null</c> when it does not move, or its canvas is larger than an image may be.</summary>
    internal static (IReadOnlyList<Frame>? Frames, TimeSpan Duration) FromGif(byte[] bytes)
    {
        if (Layout(bytes) is not { Moves: true } layout || layout.Width <= 0 || layout.Height <= 0
            || (long)layout.Width * layout.Height > ChatImages.MaxPixels)
            return (null, TimeSpan.Zero);
        var starts = new List<TimeSpan>(layout.Delays.Count);
        var at = TimeSpan.Zero;
        foreach (var delay in layout.Delays) { starts.Add(at); at += delay; }
        var kept = Enumerable.Range(0, Count).Select(i => (int)((long)i * (layout.Delays.Count - 1) / (Count - 1))).ToHashSet();
        var last = kept.Max();
        var frames = new List<Frame>();
        try
        {
            using var stream = new MemoryStream(bytes);
            var index = 0;
            foreach (var frame in ImageResult.AnimatedGifFramesFromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha))
            {
                // The decoder reuses its buffer: only a frame kept is copied, never all of them.
                if (kept.Contains(index))
                    frames.Add(new Frame(starts[Math.Min(index, starts.Count - 1)], (byte[])frame.Data.Clone(), frame.Width, frame.Height, Rgba: true));
                if (++index > last) break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            Diagnostics.Swallow("VideoFrames.Gif", ex);
            return (null, at);
        }
        return (frames.Count > 0 ? frames : null, at);
    }

    // ── A video, by the system ──

    /// <summary>The frames at even times, by the decoder of this system; or why there are none.</summary>
    internal static async Task<(IReadOnlyList<Frame>? Frames, TimeSpan Duration, string? Why)> ExtractAsync(byte[] bytes, string extension, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-video-");
        try
        {
            var input = Path.Combine(dir.FullName, "in" + extension);
            await File.WriteAllBytesAsync(input, bytes, ct).ConfigureAwait(false);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(Budget);
            try
            {
                return OperatingSystem.IsWindows()
                    ? await WindowsAsync(dir.FullName, input, budget.Token).ConfigureAwait(false)
                    : await FfmpegAsync(dir.FullName, input, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, TimeSpan.Zero, $"reading it took longer than {Budget.TotalSeconds:0} s");
            }
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { /* cleanup */ }
        }
    }

    private static async Task<(IReadOnlyList<Frame>?, TimeSpan, string?)> WindowsAsync(string dir, string input, CancellationToken ct)
    {
        var script = Path.Combine(dir, "frames.ps1");
        // With a byte order mark: Windows PowerShell 5.1 reads a script without one in the ANSI code page.
        await File.WriteAllTextAsync(script, WindowsScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct).ConfigureAwait(false);
        var psi = HeicImage.Start(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                                  "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                                  "-Video", input, "-Out", dir, "-Count", Count.ToString(CultureInfo.InvariantCulture));
        var run = await ChildProcess.RunAsync(psi, null, ct).ConfigureAwait(false);
        // ⚠ Only times come back on the console: the frames are where this code put them (dir/frameN.jpg), never a path
        // read back from a console whose code page may not be the one it was written in.
        var duration = TimeSpan.Zero;
        var frames = new List<Frame>();
        foreach (var line in run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("DURATION ", StringComparison.Ordinal)
                && double.TryParse(line[9..], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                duration = TimeSpan.FromSeconds(d);
            else if (line.StartsWith("FRAME ", StringComparison.Ordinal) && line[6..].Split(' ') is [var i, var s]
                     && int.TryParse(i, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                     && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var at)
                     && Path.Combine(dir, $"frame{index}.jpg") is var path && File.Exists(path))
                frames.Add(new Frame(TimeSpan.FromSeconds(at), await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)));
        }
        if (frames.Count > 0) return (frames, duration, null);
        // 0x80D1… (no decoder for the stream) or 0xC00D… (Media Foundation): the codec's extension is missing.
        var said = run.Stderr + run.Stdout;
        return (null, duration, said.Contains("0x80D1", StringComparison.OrdinalIgnoreCase) || said.Contains("0xC00D", StringComparison.OrdinalIgnoreCase)
            ? "Windows has no decoder for this video's format: for a .webm install the VP9 Video Extensions, for HEVC the HEVC Video "
              + "Extensions, both from the Microsoft Store — or save the recording as an .mp4 (H.264)"
            : "Windows could not read it: " + Tail(said));
    }

    /// <summary>Opens the video with Windows' media stack (WinRT) and writes <c>Count</c> frames at even times as
    /// <c>Out/frameN.jpg</c>; prints <c>DURATION s</c>, then <c>FRAME N s</c> per frame; a failure as one line, its HRESULT
    /// with it.</summary>
    private const string WindowsScript = """
        param([string]$Video, [string]$Out, [int]$Count = 8)
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
        trap { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
               [Console]::Error.WriteLine(('{0} (HRESULT 0x{1:X8})' -f $e.Message.Trim(), $e.HResult)); exit 1 }
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
        function Await($op, [type]$t) { $asTask.MakeGenericMethod($t).Invoke($null, @($op)).GetAwaiter().GetResult() }
        [void][Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
        [void][Windows.Media.Editing.MediaClip, Windows.Media.Editing, ContentType = WindowsRuntime]
        [void][Windows.Media.Editing.MediaComposition, Windows.Media.Editing, ContentType = WindowsRuntime]
        $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Video)) ([Windows.Storage.StorageFile])
        $clip = Await ([Windows.Media.Editing.MediaClip]::CreateFromFileAsync($file)) ([Windows.Media.Editing.MediaClip])
        $comp = New-Object Windows.Media.Editing.MediaComposition
        # The clips list is a WinRT vector Windows PowerShell does not project: its Add is called through its interface.
        [void][System.Collections.Generic.ICollection[Windows.Media.Editing.MediaClip]].GetMethod('Add').Invoke($comp.Clips, @($clip))
        $duration = $comp.Duration
        'DURATION ' + $duration.TotalSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
        for ($i = 0; $i -lt $Count; $i++) {
            $at = [TimeSpan]::FromTicks([long]($duration.Ticks * ($i + 0.5) / $Count))
            $stream = Await ($comp.GetThumbnailAsync($at, 0, 0, [Windows.Media.Editing.VideoFramePrecision]::NearestFrame)) ([Windows.Graphics.Imaging.ImageStream])
            $reader = New-Object Windows.Storage.Streams.DataReader($stream.GetInputStreamAt(0))
            [void](Await ($reader.LoadAsync([uint32]$stream.Size)) ([uint32]))
            $bytes = New-Object byte[] $stream.Size
            $reader.ReadBytes($bytes)
            [IO.File]::WriteAllBytes((Join-Path $Out ('frame' + $i + '.jpg')), $bytes)
            'FRAME ' + $i + ' ' + $at.TotalSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
        }
        """;

    /// <summary>macOS and Linux: one ffmpeg call for every frame, under one budget. The duration from ffprobe when the file
    /// states it; a recording that does not (a browser's .webm) is sampled once a second instead.</summary>
    private static async Task<(IReadOnlyList<Frame>?, TimeSpan, string?)> FfmpegAsync(string dir, string input, CancellationToken ct)
    {
        // A graphical app's PATH lacks Homebrew's folders on macOS: they are looked in too.
        static string? Find(string tool) => HeicImage.OnPath(tool)
            ?? new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" }.Select(f => Path.Combine(f, tool)).FirstOrDefault(File.Exists);
        if (Find("ffmpeg") is not { } ffmpeg)
            return (null, TimeSpan.Zero, OperatingSystem.IsMacOS()
                ? "reading a video needs ffmpeg, which is not installed: brew install ffmpeg"
                : "reading a video needs ffmpeg, which is not installed: sudo apt install ffmpeg (or your distribution's package)");
        double seconds = 0;
        if (Find("ffprobe") is { } ffprobe)
        {
            var probe = await ChildProcess.RunAsync(HeicImage.Start(ffprobe, "-v", "error", "-show_entries", "format=duration",
                                                                    "-of", "csv=p=0", input), null, ct).ConfigureAwait(false);
            _ = double.TryParse(probe.Stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
        }
        // Frames at even times when the duration is known; once a second otherwise, at most ten minutes of them.
        var rate = seconds > 0 ? Count / seconds : 1.0;
        var run = await ChildProcess.RunAsync(HeicImage.Start(ffmpeg, "-v", "error", "-y", "-i", input, "-vf",
                                                              "fps=" + rate.ToString("0.######", CultureInfo.InvariantCulture),
                                                              "-frames:v", seconds > 0 ? (Count + 1).ToString(CultureInfo.InvariantCulture) : "600",
                                                              Path.Combine(dir, "f%04d.png")), null, ct).ConfigureAwait(false);
        var files = Directory.EnumerateFiles(dir, "f*.png").Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0) return (null, TimeSpan.FromSeconds(seconds), "ffmpeg could not read it: " + Tail(run.Stderr + run.Stdout));
        var picked = Enumerable.Range(0, Math.Min(Count, files.Count))
            .Select(i => files.Count <= Count ? i : (int)((long)i * (files.Count - 1) / (Count - 1))).Distinct().ToList();
        var frames = new List<Frame>();
        foreach (var i in picked)
            frames.Add(new Frame(TimeSpan.FromSeconds((i + 0.5) / rate), await File.ReadAllBytesAsync(files[i], ct).ConfigureAwait(false)));
        return (frames, TimeSpan.FromSeconds(seconds > 0 ? seconds : files.Count / rate), null);
    }

    // ── The contact sheet ──

    /// <summary>The frames side by side, four to a row, each with its OWN time written in its corner, as one JPEG, and how
    /// many it holds; <c>null</c> when none decodes.</summary>
    internal static (byte[] Image, int Frames)? Compose(IReadOnlyList<Frame> frames)
    {
        // Each cell keeps the time of the frame it shows: a frame that does not decode is dropped WITH its time.
        var cells = frames.Select(f => (f.At, Cell: Decode(f))).Where(c => c.Cell.Pixels is not null).ToList();
        if (cells.Count == 0) return null;
        var tenths = cells.Zip(cells.Skip(1)).Any(p => (p.Second.At - p.First.At).TotalSeconds < 2);
        var columns = Math.Min(4, cells.Count);
        var rows = (cells.Count + columns - 1) / columns;
        var cellHeight = cells.Max(c => c.Cell.Height);
        const int gap = 4;
        var width = columns * CellWidth + (columns - 1) * gap;
        var height = rows * cellHeight + (rows - 1) * gap;
        var sheet = new byte[checked((long)width * height * 4)];
        for (var p = 0; p < sheet.Length; p += 4) { sheet[p] = 40; sheet[p + 1] = 40; sheet[p + 2] = 44; sheet[p + 3] = 255; }
        for (var i = 0; i < cells.Count; i++)
        {
            var (x0, y0) = (i % columns * (CellWidth + gap), i / columns * (cellHeight + gap));
            var c = cells[i].Cell;
            for (var y = 0; y < c.Height; y++)
                Buffer.BlockCopy(c.Pixels!, y * c.Width * 4, sheet, ((y0 + y) * width + x0) * 4, c.Width * 4);
            Label(sheet, width, x0 + 6, y0 + 6, Time(cells[i].At, tenths));
        }
        using var output = new MemoryStream();
        new ImageWriter().WriteJpg(sheet, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output, 85);
        return (output.ToArray(), cells.Count);
    }

    /// <summary>A frame as RGBA fitted in a <see cref="CellWidth"/> × <see cref="CellMaxHeight"/> box; <c>null</c> pixels when
    /// it does not decode or is larger than an image may be.</summary>
    private static (byte[]? Pixels, int Width, int Height) Decode(Frame frame)
    {
        byte[] rgba; int w, h;
        try
        {
            if (frame.Rgba) (rgba, w, h) = (frame.Image, frame.Width, frame.Height);
            else
            {
                // Its size from its header first: a frame that declares more pixels than an image may have is not decoded.
                var header = ImageInfo.FromStream(new MemoryStream(frame.Image));
                if (header is not { } info || (long)info.Width * info.Height > ChatImages.MaxPixels) return (null, 0, 0);
                var image = ImageResult.FromMemory(frame.Image, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                (rgba, w, h) = (image.Data, image.Width, image.Height);
            }
            if (w <= 0 || h <= 0) return (null, 0, 0);
            var scale = Math.Min((double)CellWidth / w, (double)CellMaxHeight / h);
            var (cw, ch) = (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
            var fitted = new byte[cw * ch * 4];
            StbImageResize.stbir_resize_uint8(rgba, w, h, w * 4, fitted, cw, ch, cw * 4, 4);
            if (cw == CellWidth) return (fitted, cw, ch);
            // Narrower than a cell (a tall frame): centred on the sheet's ground.
            var cell = new byte[CellWidth * ch * 4];
            for (var p = 0; p < cell.Length; p += 4) { cell[p] = 40; cell[p + 1] = 40; cell[p + 2] = 44; cell[p + 3] = 255; }
            var left = (CellWidth - cw) / 2;
            for (var y = 0; y < ch; y++) Buffer.BlockCopy(fitted, y * cw * 4, cell, (y * CellWidth + left) * 4, cw * 4);
            return (cell, CellWidth, ch);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            Diagnostics.Swallow("VideoFrames.Decode", ex);
            return (null, 0, 0);
        }
    }

    /// <summary>A time as the sheet and the chip write it: <c>0:30</c>, <c>1:05:02</c>.</summary>
    /// <param name="t">The time.</param>
    /// <param name="tenths">With tenths of a second (<c>0:01.5</c>): frames under two seconds apart would read the same time.</param>
    internal static string Time(TimeSpan t, bool tenths = false) =>
        (t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : $"{(int)t.TotalMinutes}:{t.Seconds:00}")
        + (tenths ? "." + (t.Milliseconds / 100).ToString(CultureInfo.InvariantCulture) : string.Empty);

    // A 5 × 7 font for the digits, the colon and the dot: the time drawn on each frame, readable by the model, no font shipped.
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ['3'] = ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ['5'] = ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
        ['6'] = ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
        ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ['8'] = ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
        ['9'] = ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
        [':'] = ["00000", "00100", "00100", "00000", "00100", "00100", "00000"],
        ['.'] = ["00000", "00000", "00000", "00000", "00000", "01100", "01100"],
    };

    /// <summary>Writes <paramref name="text"/> in white on a dark box at <paramref name="x"/>, <paramref name="y"/>.</summary>
    private static void Label(byte[] sheet, int width, int x, int y, string text)
    {
        const int scale = 3, pad = 4;
        var boxW = text.Length * 6 * scale + pad * 2 - scale;
        var boxH = 7 * scale + pad * 2;
        void Paint(int px, int py, byte r, byte g, byte b)
        {
            if (px < 0 || py < 0 || px >= width) return;
            var at = ((long)py * width + px) * 4;
            if (at + 3 >= sheet.Length) return;
            sheet[at] = r; sheet[at + 1] = g; sheet[at + 2] = b; sheet[at + 3] = 255;
        }
        for (var by = 0; by < boxH; by++) for (var bx = 0; bx < boxW; bx++) Paint(x + bx, y + by, 0, 0, 0);
        for (var c = 0; c < text.Length; c++)
        {
            if (!Glyphs.TryGetValue(text[c], out var rows)) continue;
            for (var gy = 0; gy < 7; gy++)
                for (var gx = 0; gx < 5; gx++)
                    if (rows[gy][gx] == '1')
                        for (var sy = 0; sy < scale; sy++)
                            for (var sx = 0; sx < scale; sx++)
                                Paint(x + pad + (c * 6 + gx) * scale + sx, y + pad + gy * scale + sy, 255, 255, 255);
        }
    }

    private static string Tail(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= 300 ? line : line[^300..];
    }
}
