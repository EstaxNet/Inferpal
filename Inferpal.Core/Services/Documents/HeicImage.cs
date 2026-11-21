using System.Diagnostics;
using System.Text;

namespace Inferpal.Services.Documents;

/// <summary>
/// A HEIC or HEIF image (an iPhone photo) converted to PNG by the decoder of the system it runs on — Windows' own imaging
/// component, macOS' <c>sips</c>, libheif's <c>heif-dec</c> or <c>heif-convert</c> on Linux. No server decodes HEIC.
/// </summary>
/// <remarks>⚠ The decoder may be missing (Windows without the HEIF and HEVC extensions, Linux without libheif): said with the
/// gesture that installs it, never a silent refusal. ⚠ A photo's orientation is applied — an iPhone stores it sideways:
/// Windows and libheif turn the pixels; macOS writes a JPEG that keeps the EXIF tag, which the reader then applies.</remarks>
internal static class HeicImage
{
    /// <summary>How long one conversion may take.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>Whether <paramref name="bytes"/> open as a HEIF file (its <c>ftyp</c> box names a HEIF brand).</summary>
    /// <remarks>By its brands, major and compatible: the generic <c>mif1</c> of an AVIF is no HEIC — its photo is AV1, which
    /// the HEVC advice would not help.</remarks>
    internal static bool Is(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || !bytes[4..8].SequenceEqual("ftyp"u8)) return false;
        var box = (int)Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes), (uint)Math.Min(bytes.Length, 256));
        // The major brand at 8, then the compatible ones from 16 (past the minor version).
        for (var at = 8; at + 4 <= box; at += at == 8 ? 8 : 4)
            if (Encoding.ASCII.GetString(bytes.Slice(at, 4)) is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "hevm" or "hevs")
                return true;
        return false;
    }

    /// <summary>The image as a PNG (a JPEG on macOS), or why it could not be converted (in English: the cause and what
    /// fixes it).</summary>
    internal static async Task<(byte[]? Image, string? Why)> ConvertAsync(byte[] bytes, CancellationToken ct)
    {
        DirectoryInfo? dir = null;
        try
        {
            dir = Directory.CreateTempSubdirectory("inferpal-heic-");
            var input = Path.Combine(dir.FullName, "in.heic");
            var output = Path.Combine(dir.FullName, OperatingSystem.IsMacOS() ? "out.jpg" : "out.png");
            await File.WriteAllBytesAsync(input, bytes, ct).ConfigureAwait(false);
            var (psi, missing) = Command(dir.FullName, input, output);
            if (psi is null) return (null, missing);
            var run = await ChildProcess.RunAsync(psi, Budget, ct).ConfigureAwait(false);
            if (run.TimedOut) return (null, $"converting it took longer than {Budget.TotalSeconds:0} s");
            // libheif names its outputs out-1.png, out-2.png… for a file of several images: the first is the photo.
            var written = File.Exists(output) ? output
                : Directory.EnumerateFiles(dir.FullName, Path.GetFileNameWithoutExtension(output) + "*" + Path.GetExtension(output))
                           .Order(StringComparer.Ordinal).FirstOrDefault();
            if (run.ExitCode == 0 && written is not null) return (await File.ReadAllBytesAsync(written, ct).ConfigureAwait(false), null);
            return (null, Failure(run.Stderr + run.Stdout, OperatingSystem.IsWindows()));
        }
        // A decoder that cannot start (not executable, blocked) or a temporary folder that cannot be written: a refusal
        // that says so, never an exception out of an attachment.
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("HeicImage.Convert", ex);
            return (null, "the system's decoder could not be run: " + ex.Message);
        }
        finally
        {
            try { dir?.Delete(recursive: true); } catch { /* cleanup */ }
        }
    }

    /// <summary>The command that converts on this system, or why there is none.</summary>
    private static (ProcessStartInfo? Psi, string? Missing) Command(string dir, string input, string output)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(dir, "heic.ps1");
            // With a byte order mark: Windows PowerShell 5.1 reads a script without one in the ANSI code page.
            File.WriteAllText(script, WindowsScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return (Start(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                          "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-In", input, "-Out", output), null);
        }
        if (OperatingSystem.IsMacOS()) return (Start("/usr/bin/sips", "-s", "format", "jpeg", input, "--out", output), null);
        foreach (var tool in new[] { "heif-dec", "heif-convert" })
            if (OnPath(tool) is { } found) return (Start(found, input, output), null);
        return (null, "this system has no HEIC decoder: install libheif (sudo apt install libheif-examples, "
                    + "sudo dnf install libheif-tools, or sudo pacman -S libheif)");
    }

    /// <summary>What a failed conversion means, in a sentence: Windows without its HEIF codec says it in a code.</summary>
    internal static string Failure(string output, bool windows)
    {
        if (windows && (output.Contains("88982F50", StringComparison.OrdinalIgnoreCase)
                        || output.Contains("component cannot be found", StringComparison.OrdinalIgnoreCase)))
            return "Windows has no HEIC decoder here: install \"HEIF Image Extensions\" and \"HEVC Video Extensions\" from the "
                 + "Microsoft Store";
        // Its first line: a decoder's own words, never a stack.
        var detail = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        // ⚠ On Windows the HEIF extension decodes the container and the HEVC one the photo: with only the first, the failure
        // carries another code — the advice names both, whatever the code.
        return "the system could not convert it" + (detail.Length > 0 ? ": " + SafeTruncate.Truncate(detail, 300) : string.Empty)
             + (windows ? ". A HEIC photo needs both \"HEIF Image Extensions\" and \"HEVC Video Extensions\" from the Microsoft Store" : string.Empty);
    }

    internal static string? OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);

    internal static ProcessStartInfo Start(string file, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Windows: the image decoded by the system's imaging component (WinRT), its EXIF orientation applied, written
    /// as a PNG.</summary>
    private const string WindowsScript = """
        param([string]$In, [string]$Out)
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapEncoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
        $op = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
        $action = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
        # GetResult, not Wait: Wait wraps a failure in "One or more errors occurred", hiding the HRESULT that names the cure.
        # One line, in UTF-8: the innermost exception and its HRESULT (0x88982F50 = no HEIF decoder installed).
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
        trap { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
               [Console]::Error.WriteLine(('{0} (HRESULT 0x{1:X8})' -f $e.Message.Trim(), $e.HResult)); exit 1 }
        function Wait-Op($o, [type]$t) { $op.MakeGenericMethod($t).Invoke($null, @($o)).GetAwaiter().GetResult() }
        function Wait-Action($o) { $action.Invoke($null, @($o)).GetAwaiter().GetResult() }

        $file = Wait-Op ([Windows.Storage.StorageFile]::GetFileFromPathAsync($In)) ([Windows.Storage.StorageFile])
        $stream = Wait-Op ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Wait-Op ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Wait-Op ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,
            [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied, (New-Object Windows.Graphics.Imaging.BitmapTransform),
            [Windows.Graphics.Imaging.ExifOrientationMode]::RespectExifOrientation,
            [Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage)) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $folder = Wait-Op ([Windows.Storage.StorageFolder]::GetFolderFromPathAsync((Split-Path $Out))) ([Windows.Storage.StorageFolder])
        $target = Wait-Op ($folder.CreateFileAsync((Split-Path $Out -Leaf), [Windows.Storage.CreationCollisionOption]::ReplaceExisting)) ([Windows.Storage.StorageFile])
        $written = Wait-Op ($target.OpenAsync([Windows.Storage.FileAccessMode]::ReadWrite)) ([Windows.Storage.Streams.IRandomAccessStream])
        $encoder = Wait-Op ([Windows.Graphics.Imaging.BitmapEncoder]::CreateAsync([Windows.Graphics.Imaging.BitmapEncoder]::PngEncoderId, $written)) ([Windows.Graphics.Imaging.BitmapEncoder])
        $encoder.SetSoftwareBitmap($bitmap)
        Wait-Action ($encoder.FlushAsync())
        $written.Dispose(); $stream.Dispose()
        """;
}
