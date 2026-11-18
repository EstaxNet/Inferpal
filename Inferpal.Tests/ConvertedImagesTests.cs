using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The images no server decodes, attached anyway: a TIFF drawn here and sent as a PNG, a HEIC photo converted by the
/// system's own decoder — or, where there is none, a refusal that names what installs it.
/// </summary>
public class ConvertedImagesTests
{
    private static string Fixture(string kind, string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "images", kind, name);
    }

    [Fact]
    public void ATiff_IsAttachedAsAPng()
    {
        var read = ChatImages.Read(File.ReadAllBytes(Fixture("tiff", "rgba-deflate.tif")), "scan.tif");

        Assert.Null(read.Refusal);
        Assert.Equal("image/png", read.Image!.MediaType);
        Assert.Equal((40, 24), (read.Image.Width, read.Image.Height));
        Assert.Equal("scan.tif", read.Image.Name);
    }

    [Fact]
    public void ATiffOfSeveralPages_AttachesTheFirst_AndSaysSo()
    {
        var read = ChatImages.Read(File.ReadAllBytes(Fixture("tiff", "two-pages.tif")), "fax.tif");

        Assert.NotNull(read.Image);
        Assert.Equal(Strings.ImageFirstPageOnly("fax.tif", 2), read.Note);
    }

    [Fact]
    public void ATiffItCannotDraw_IsRefusedWithItsCause()
    {
        var bytes = File.ReadAllBytes(Fixture("tiff", "gray-lzw.tif"));
        var ifd = BitConverter.ToInt32(bytes, 4);
        for (var i = 0; i < BitConverter.ToUInt16(bytes, ifd); i++)
            if (BitConverter.ToUInt16(bytes, ifd + 2 + i * 12) == 259) BitConverter.GetBytes((ushort)7).CopyTo(bytes, ifd + 2 + i * 12 + 8);

        var read = ChatImages.Read(bytes, "photo.tif");

        Assert.Null(read.Image);
        Assert.Equal(Strings.ImageNotDecoded("photo.tif", "it is compressed in JPEG inside the TIFF, which is not read"), read.Refusal);
    }

    [Fact]
    public async Task AHeicPhoto_IsConvertedByTheSystem_OrTheRefusalNamesWhatInstallsItsDecoder()
    {
        var read = await ChatImages.ReadAsync(File.ReadAllBytes(Fixture("heic", "form.heic")), "IMG_0042.HEIC", CancellationToken.None);

        if (read.Image is { } image)
        {
            Assert.Equal("image/jpeg", image.MediaType);                    // a photo: sent as a JPEG
            Assert.Equal((1000, 360), (image.Width, image.Height));
        }
        else
        {
            // Never a silent or generic refusal: what installs the decoder, on the system the suite runs on.
            Assert.Matches(OperatingSystem.IsWindows() ? "HEIF Image Extensions" : OperatingSystem.IsMacOS() ? "convert" : "libheif",
                           read.Refusal);
        }
    }

    [Fact]
    public async Task ADamagedHeic_IsRefusedWithACause_NeverAnException()
    {
        byte[] damaged = [0, 0, 0, 0x18, .. "ftypheic"u8, 0, 0, 0, 0, .. "mif1heic"u8, .. new byte[200]];

        var read = await ChatImages.ReadAsync(damaged, "IMG_0001.HEIC", CancellationToken.None);

        Assert.Null(read.Image);
        Assert.StartsWith(Strings.ImageNotDecoded("IMG_0001.HEIC", "\u0001").Split('\u0001')[0], read.Refusal);
    }

    [Theory]
    [InlineData("ftypheic", "", true)]
    [InlineData("ftypmif1", "mif1heic", true)]       // the generic brand, HEIC among the compatible ones
    [InlineData("ftypavif", "mif1avif", false)]      // an AVIF: its photo is AV1, not HEVC
    [InlineData("ftypmif1", "mif1avif", false)]
    public void AHeic_IsKnownByItsBrands(string major, string compatible, bool heic)
    {
        var size = 16 + compatible.Length;
        byte[] bytes = [0, 0, 0, (byte)size, .. System.Text.Encoding.ASCII.GetBytes(major), 0, 0, 0, 0,
                        .. System.Text.Encoding.ASCII.GetBytes(compatible), .. new byte[16]];

        Assert.Equal(heic, HeicImage.Is(bytes));
    }

    [Fact]
    public void OnWindows_AFailedConversion_NamesBothExtensions_WhateverItsCode()
    {
        Assert.Contains("install \"HEIF Image Extensions\"", HeicImage.Failure("No imaging component (HRESULT 0x88982F50)", windows: true));
        // HEIF there, HEVC missing: another code — the advice still names the HEVC extension.
        Assert.Contains("\"HEVC Video Extensions\"", HeicImage.Failure("The codec is missing (HRESULT 0xC00D5212)", windows: true));
        // Elsewhere, the decoder's own first line, no Store.
        var linux = HeicImage.Failure("Could not decode HEIF/AVIF image\n  at line 2", windows: false);
        Assert.Equal("the system could not convert it: Could not decode HEIF/AVIF image", linux);
    }

    [Fact]
    public async Task TheOcrTool_ReadsATiff_ItNoLongerRefusesTheFormat()
    {
        var root = Directory.CreateTempSubdirectory("inferpal-ocr-tiff-");
        try
        {
            File.Copy(Fixture("tiff", "gray-lzw.tif"), Path.Combine(root.FullName, "scan.tif"));
            var read = await new Inferpal.Services.Tools.OcrTool(() => root.FullName)
                .ExecuteAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = "scan.tif" }), CancellationToken.None);

            Assert.DoesNotContain("not an image the OCR reads", read);
            Assert.Contains("scan.tif", read);
        }
        finally { try { root.Delete(recursive: true); } catch { } }
    }

    [Fact]
    public async Task TheSynchronousReader_StillRefusesAHeic_ItNeedsTheSystemsDecoder()
    {
        // Through ReadAsync only: what is not a HEIC goes through the ordinary reader, unchanged.
        var png = await ChatImages.ReadAsync(File.ReadAllBytes(Fixture("tiff", "gray-lzw.png")), "a.png", CancellationToken.None);
        Assert.Equal("image/png", png.Image!.MediaType);

        var heic = ChatImages.Read(File.ReadAllBytes(Fixture("heic", "form.heic")), "IMG_0042.HEIC");
        Assert.Equal(Strings.ImageFormatNotSent("IMG_0042.HEIC", "HEIC"), heic.Refusal);
    }
}
