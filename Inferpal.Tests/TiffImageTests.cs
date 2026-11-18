using System.IO;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The TIFF decoder (<c>TiffImage</c>) against a reference: each fixture is decoded and compared, pixel by pixel, with the
/// PNG Pillow decodes it to — the kinds Pillow writes, plus tiles, big-endian and the predictor written by hand.
/// </summary>
public class TiffImageTests
{
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "images", "tiff", name);
    }

    [Theory]
    [InlineData("gray-lzw")]
    [InlineData("gray-alpha-raw")]
    [InlineData("rgba-deflate")]
    [InlineData("palette-packbits")]
    [InlineData("bilevel-raw")]
    [InlineData("gray16-raw")]
    [InlineData("cmyk-lzw")]
    [InlineData("tiled-deflate")]
    [InlineData("bigendian-predictor-deflate")]
    [InlineData("tiled-predictor-deflate")]
    [InlineData("noise-lzw")]                       // the LZW table filled past 4 096 codes: clear codes and width resets
    [InlineData("bilevel-oddwidth-raw")]            // 1 bit at an odd width: rows padded to a byte
    public void EachKind_DecodesToThePixelsAReferenceDecoderGives(string name)
    {
        var decoded = TiffImage.Decode(File.ReadAllBytes(Fixture(name + ".tif")), 40_000_000);
        var expected = StbImageSharp.ImageResult.FromMemory(File.ReadAllBytes(Fixture(name + ".png")),
                                                            StbImageSharp.ColorComponents.RedGreenBlueAlpha);

        Assert.Null(decoded.Refusal);
        Assert.Equal((expected.Width, expected.Height), (decoded.Width, decoded.Height));
        var worst = 0;
        for (var i = 0; i < expected.Data.Length; i++) worst = Math.Max(worst, Math.Abs(expected.Data[i] - decoded.Rgba![i]));
        Assert.True(worst <= 1, $"{name}: a channel differs by {worst}");
    }

    [Fact]
    public void AFileOfSeveralPages_DecodesTheFirst_AndCountsThem()
    {
        var decoded = TiffImage.Decode(File.ReadAllBytes(Fixture("two-pages.tif")), 40_000_000);
        var first = StbImageSharp.ImageResult.FromMemory(File.ReadAllBytes(Fixture("two-pages.png")),
                                                         StbImageSharp.ColorComponents.RedGreenBlueAlpha);

        Assert.Equal(2, decoded.Pages);
        Assert.Equal(first.Data, decoded.Rgba);
    }

    [Fact]
    public void WhatItDoesNotRead_IsSaid_NeverDrawnWrong()
    {
        var bytes = File.ReadAllBytes(Fixture("gray-lzw.tif"));
        // The compression tag (259) of the one directory, set to JPEG (7).
        var ifd = BitConverter.ToInt32(bytes, 4);
        var count = BitConverter.ToUInt16(bytes, ifd);
        for (var i = 0; i < count; i++)
        {
            var e = ifd + 2 + i * 12;
            if (BitConverter.ToUInt16(bytes, e) == 259) BitConverter.GetBytes((ushort)7).CopyTo(bytes, e + 8);
        }

        var decoded = TiffImage.Decode(bytes, 40_000_000);

        Assert.Null(decoded.Rgba);
        Assert.Contains("JPEG", decoded.Refusal);
    }

    [Fact]
    public void ASizePastTheLimit_IsRefusedBeforeAnythingIsAllocated()
    {
        var decoded = TiffImage.Decode(File.ReadAllBytes(Fixture("gray-lzw.tif")), maxPixels: 100);

        Assert.Null(decoded.Rgba);
        Assert.Contains("40 × 24 pixels", decoded.Refusal);
    }

    /// <summary>A little-endian TIFF of one directory with these SHORT/LONG tags (tag, type, values), and
    /// <paramref name="data"/> after the header; the values that do not fit in a field go after the directory.</summary>
    private static byte[] Craft((int Tag, int Type, long[] Values)[] tags, byte[] data)
    {
        var ifd = 8 + data.Length;
        var extra = ifd + 2 + tags.Length * 12 + 4;
        var head = new List<byte>("II*\0"u8.ToArray());
        head.AddRange(BitConverter.GetBytes(ifd));
        head.AddRange(data);
        head.AddRange(BitConverter.GetBytes((ushort)tags.Length));
        var tail = new List<byte>();
        foreach (var (tag, type, values) in tags.OrderBy(t => t.Tag))
        {
            var packed = values.SelectMany(v => type == 3 ? BitConverter.GetBytes((ushort)v) : BitConverter.GetBytes((uint)v)).ToArray();
            head.AddRange(BitConverter.GetBytes((ushort)tag));
            head.AddRange(BitConverter.GetBytes((ushort)type));
            head.AddRange(BitConverter.GetBytes(values.Length));
            if (packed.Length <= 4) head.AddRange(packed.Concat(new byte[4 - packed.Length]));
            else { head.AddRange(BitConverter.GetBytes(extra + tail.Count)); tail.AddRange(packed); }
        }
        head.AddRange(BitConverter.GetBytes(0));
        return [.. head, .. tail];
    }

    [Fact]
    public void TilesTheFileMakesHuge_AreRefused_BeforeAnythingIsAllocated()
    {
        // A 16 × 16 image declaring 23 008-pixel tiles: gigabytes per tile, from a few hundred bytes.
        var bytes = Craft([(256, 3, [16]), (257, 3, [16]), (258, 3, [8]), (259, 3, [1]), (262, 3, [1]), (277, 3, [1]),
                           (322, 4, [23008]), (323, 4, [23008]), (324, 4, [8]), (325, 4, [256])], new byte[256]);

        Assert.Equal("it is damaged (tiles)", TiffImage.Decode(bytes, 40_000_000).Refusal);
    }

    [Fact]
    public void ATagClaimingMoreValuesThanTheFileHolds_IsDamaged_NeverAnAllocation()
    {
        var bytes = Craft([(256, 3, [16]), (257, 3, [16]), (258, 3, [8]), (259, 3, [1]), (262, 3, [1]), (277, 3, [1]),
                           (273, 4, [8, 8])], new byte[256]);
        // The strip offsets (two values, after the directory) now claim a million.
        var ifd = BitConverter.ToInt32(bytes, 4);
        for (var i = 0; i < BitConverter.ToUInt16(bytes, ifd); i++)
            if (BitConverter.ToUInt16(bytes, ifd + 2 + i * 12) == 273) BitConverter.GetBytes(1_000_000).CopyTo(bytes, ifd + 2 + i * 12 + 4);

        Assert.Equal("it is damaged", TiffImage.Decode(bytes, 40_000_000).Refusal);
    }

    [Fact]
    public void ImageDataThatStopsShort_IsRefused_NeverDrawnBlack()
    {
        // 16 × 16 gray, uncompressed: 256 bytes of samples, only 100 there.
        var bytes = Craft([(256, 3, [16]), (257, 3, [16]), (258, 3, [8]), (259, 3, [1]), (262, 3, [1]), (277, 3, [1]),
                           (273, 4, [8]), (278, 3, [16]), (279, 4, [100])], new byte[100]);

        Assert.Equal("it is cut: part of its image data is missing", TiffImage.Decode(bytes, 40_000_000).Refusal);
    }

    [Fact]
    public void StripsMissingForPartOfTheImage_AreRefused_NeverDrawnBlack()
    {
        // 16 rows in strips of 4: four strips needed, one given.
        var bytes = Craft([(256, 3, [16]), (257, 3, [16]), (258, 3, [8]), (259, 3, [1]), (262, 3, [1]), (277, 3, [1]),
                           (273, 4, [8]), (278, 3, [4]), (279, 4, [64])], new byte[64]);

        Assert.Equal("it is cut: part of its image data is missing", TiffImage.Decode(bytes, 40_000_000).Refusal);
    }

    [Theory]
    [InlineData(0, 255)]        // an unspecified extra sample is another channel: the picture stays opaque
    [InlineData(2, 64)]         // unassociated alpha
    public void AnExtraSample_IsAlphaOnlyWhenTheFileSaysSo(int extraSamples, int alpha)
    {
        var pixel = new byte[] { 200, 100, 50, 64 };
        var bytes = Craft([(256, 3, [1]), (257, 3, [1]), (258, 3, [8, 8, 8, 8]), (259, 3, [1]), (262, 3, [2]), (277, 3, [4]),
                           (273, 4, [8]), (279, 4, [4]), (338, 3, [extraSamples])], pixel);

        Assert.Equal(alpha, TiffImage.Decode(bytes, 40_000_000).Rgba![3]);
    }

    [Fact]
    public void AssociatedAlpha_IsUnpremultiplied()
    {
        // Half-transparent white, written premultiplied: 128, 128, 128, 128.
        var bytes = Craft([(256, 3, [1]), (257, 3, [1]), (258, 3, [8, 8, 8, 8]), (259, 3, [1]), (262, 3, [2]), (277, 3, [4]),
                           (273, 4, [8]), (279, 4, [4]), (338, 3, [1])], [128, 128, 128, 128]);

        Assert.Equal(new byte[] { 255, 255, 255, 128 }, TiffImage.Decode(bytes, 40_000_000).Rgba);
    }

    [Theory]
    [InlineData(3, 16, 1, 1, "its palette is not read")]                  // a 16-bit palette
    [InlineData(1, 4, 1, 2, "its predictor is not read at this sample size")]
    [InlineData(5, 8, 3, 1, "its colour samples are incomplete")]          // CMYK of three samples
    public void APixelFormatItWouldDrawWrong_IsRefused(int photometric, int bps, int spp, int predictor, string why)
    {
        var bytes = Craft([(256, 3, [16]), (257, 3, [16]), (258, 3, Enumerable.Repeat((long)bps, spp).ToArray()), (259, 3, [1]),
                           (262, 3, [photometric]), (277, 3, [spp]), (317, 3, [predictor]), (273, 4, [8]), (279, 4, [4])],
                          new byte[4]);

        Assert.Equal(why, TiffImage.Decode(bytes, 40_000_000).Refusal);
    }

    [Fact]
    public void ADamagedFile_IsSaidDamaged()
    {
        var bytes = File.ReadAllBytes(Fixture("gray-lzw.tif"));
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 4);   // the first directory, far past the end

        Assert.Equal("it is damaged", TiffImage.Decode(bytes, 40_000_000).Refusal);
    }
}
