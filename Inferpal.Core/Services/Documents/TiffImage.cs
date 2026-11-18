using System.Buffers.Binary;
using System.IO.Compression;

namespace Inferpal.Services.Documents;

/// <summary>A TIFF decoded to RGBA pixels (its first page), or why it is not: <see cref="Refusal"/> is the cause, in English.</summary>
/// <param name="Pages">How many pages the file holds; only the first is decoded.</param>
internal sealed record TiffDecoded(byte[]? Rgba, int Width, int Height, int Pages, string? Refusal);

/// <summary>
/// The baseline of TIFF, decoded on every system with no package: uncompressed, PackBits, LZW and Deflate data, with or
/// without the horizontal predictor; gray, RGB, RGBA, CMYK and palette pixels of 1 to 16 bits; strips and tiles.
/// </summary>
/// <remarks>⚠ What it does not read is said, never drawn wrong: a page compressed in JPEG or as a fax (CCITT), planes stored
/// apart, floating-point samples, BigTIFF. ⚠ Sizes come from the file: the pixel count is checked before anything is
/// allocated, and each strip or tile is decoded into the size the image gives it, never the size its data claims.</remarks>
internal static class TiffImage
{
    /// <summary>Whether <paramref name="bytes"/> open as a TIFF (either byte order).</summary>
    internal static bool Is(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && (bytes[..4].SequenceEqual("II*\0"u8) || bytes[..4].SequenceEqual("MM\0*"u8));

    /// <summary>The first page of the TIFF in <paramref name="bytes"/>, at most <paramref name="maxPixels"/> pixels.</summary>
    internal static TiffDecoded Decode(byte[] bytes, long maxPixels)
    {
        if (bytes.Length >= 4 && (bytes.AsSpan(0, 4).SequenceEqual("II+\0"u8) || bytes.AsSpan(0, 4).SequenceEqual("MM\0+"u8)))
            return Refused("it is a BigTIFF, which is not read");
        if (!Is(bytes)) return Refused("it is not a TIFF");
        try
        {
            var reader = new Reader(bytes, little: bytes[0] == (byte)'I');
            var first = reader.U32(4);
            var pages = reader.CountPages(first);
            var tags = reader.Tags(first);
            return reader.Image(tags, pages, maxPixels);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            Diagnostics.Swallow("TiffImage.Decode", ex);
            return Refused("it is damaged");
        }
    }

    private static TiffDecoded Refused(string why) => new(null, 0, 0, 0, why);

    private sealed class Reader(byte[] b, bool little)
    {
        /// <summary>The tags the decoder reads: size, compression, layout, strips and tiles, palette, samples.</summary>
        private static readonly HashSet<int> Read = [256, 257, 258, 259, 262, 273, 277, 278, 279, 284, 317, 320, 322, 323, 324, 325, 338, 339];

        public uint U16(long at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(checked((int)at), 2))
                                           : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(checked((int)at), 2));

        public uint U32(long at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(checked((int)at), 4))
                                           : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(checked((int)at), 4));

        /// <summary>The pages, counted along the chain of directories — a thumbnail or a mask (NewSubfileType) is no page; a
        /// chain that loops stops where it loops.</summary>
        public int CountPages(uint first)
        {
            var seen = new HashSet<uint>();
            var pages = 0;
            var at = first;
            while (at != 0 && at + 2 <= b.Length && seen.Add(at) && seen.Count < 10_000)
            {
                var count = U16(at);
                var next = at + 2 + count * 12L;
                if (next + 4 > b.Length) break;
                var subfile = 0L;
                for (var i = 0; i < count; i++)
                    if (U16(at + 2 + i * 12L) == 254) subfile = U32(at + 2 + i * 12L + 8);
                if ((subfile & 0b101) == 0) pages++;
                at = U32(next);
            }
            return Math.Max(1, pages);
        }

        /// <summary>The directory at <paramref name="at"/>: each tag's values, as numbers.</summary>
        public Dictionary<int, long[]> Tags(uint at)
        {
            var tags = new Dictionary<int, long[]>();
            var count = (int)U16(at);
            for (var i = 0; i < count; i++)
            {
                var e = at + 2 + i * 12L;
                var tag = (int)U16(e);
                // ⚠ Only the tags read below, each once, its values no more than the file holds: a directory of 65 535
                // entries each claiming a million values would otherwise allocate gigabytes from a small file.
                if (!Read.Contains(tag) || tags.ContainsKey(tag)) continue;
                var type = (int)U16(e + 2);
                var n = U32(e + 4);
                var size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 13 => 4, _ => 0 };
                if (size == 0 || n == 0) continue;               // rationals, floats and the like: not needed
                var data = n * size <= 4 ? e + 8 : U32(e + 8);
                if (data + (long)n * size > b.Length) throw new InvalidDataException("tag values outside the file");
                var values = new long[n];
                for (var k = 0; k < n; k++)
                {
                    var p = data + k * size;
                    values[k] = size switch { 1 => b[checked((int)p)], 2 => U16(p), _ => U32(p) };
                }
                tags[tag] = values;
            }
            return tags;
        }

        public TiffDecoded Image(Dictionary<int, long[]> t, int pages, long maxPixels)
        {
            long One(int tag, long otherwise) => t.TryGetValue(tag, out var v) && v.Length > 0 ? v[0] : otherwise;

            var width = (int)One(256, 0);
            var height = (int)One(257, 0);
            if (width <= 0 || height <= 0) return Refused("it is damaged (no size)");
            if ((long)width * height > maxPixels) return Refused($"it is {width} × {height} pixels, more than can be sent");
            var compression = (int)One(259, 1);
            var photometric = (int)One(262, 1);
            var spp = (int)One(277, 1);
            var bps = (int)One(258, 1);
            var planar = (int)One(284, 1);
            var predictor = (int)One(317, 1);
            if (One(339, 1) == 3) return Refused("its samples are floating-point numbers, which are not read");
            if (planar == 2 && spp > 1) return Refused("its colour planes are stored apart, which is not read");
            if (compression is 6 or 7) return Refused("it is compressed in JPEG inside the TIFF, which is not read");
            if (compression is 2 or 3 or 4) return Refused("it is a fax image (CCITT), which is not read");
            if (compression is not (1 or 5 or 8 or 32946 or 32773)) return Refused($"its compression ({compression}) is not read");
            if (bps is not (1 or 2 or 4 or 8 or 16) || spp is < 1 or > 8) return Refused("its pixel format is not read");
            if (photometric is not (0 or 1 or 2 or 3 or 5)) return Refused("its colour space is not read");
            if (photometric == 3 && (bps > 8 || !t.TryGetValue(320, out var map) || map.Length < 3 << bps)) return Refused("its palette is not read");
            if (photometric == 2 && spp < 3 || photometric == 5 && spp < 4) return Refused("its colour samples are incomplete");
            if (predictor == 2 && bps is not (8 or 16)) return Refused("its predictor is not read at this sample size");

            var rowBytes = ((long)width * spp * bps + 7) / 8;
            if (rowBytes * height > MaxPlaneBytes) return Refused($"it is {width} × {height} pixels of {spp * bps} bits, more than can be read");
            var plane = new byte[checked((int)(rowBytes * height))];
            var tiled = t.ContainsKey(322) && t.ContainsKey(324);
            var offsets = t.GetValueOrDefault(tiled ? 324 : 273) ?? [];
            var counts = t.GetValueOrDefault(tiled ? 325 : 279) ?? [];
            if (offsets.Length == 0 || counts.Length < offsets.Length) return Refused("it is damaged (no image data)");

            // ⚠ Work bounded by the image, never by what the file declares: inflated data past twice the image (tiles
            // overhang its edges) means sizes that lie — a one-row image in huge tiles inflates thousands of times its size.
            var budget = 2 * rowBytes * height + 16L * 1024 * 1024;
            if (!tiled)
            {
                var rowsPerStrip = (int)Math.Min(One(278, height), height);
                if (rowsPerStrip <= 0) rowsPerStrip = height;
                if (offsets.Length < (height + (long)rowsPerStrip - 1) / rowsPerStrip) return Refused(Cut);
                for (var s = 0; s < offsets.Length; s++)
                {
                    var firstRow = (long)s * rowsPerStrip;
                    if (firstRow >= height) break;
                    var rows = (int)Math.Min(rowsPerStrip, height - firstRow);
                    var block = Inflate(offsets[s], counts[s], compression, checked((int)(rows * rowBytes)), out var whole);
                    if (!whole) return Refused(Cut);
                    if (predictor == 2) Undifference(block, rows, rowBytes, width, spp, bps);
                    block.CopyTo(plane, firstRow * rowBytes);
                }
            }
            else
            {
                var tileW = (int)One(322, 0);
                var tileH = (int)One(323, 0);
                // ⚠ A tile's size is the file's word, not the image's: bounded, or a 16 × 16 image declaring 23 000-pixel
                // tiles allocates gigabytes per tile.
                if (tileW <= 0 || tileH <= 0 || tileW % 8 != 0 || tileW > MaxTileSide || tileH > MaxTileSide)
                    return Refused("it is damaged (tiles)");
                var tileRowBytes = ((long)tileW * spp * bps + 7) / 8;
                var across = (width + tileW - 1) / tileW;
                var down = (height + tileH - 1) / tileH;
                if (offsets.Length < (long)across * down) return Refused(Cut);
                for (var i = 0; i < (long)across * down; i++)
                {
                    var tx = i % across * tileW;
                    var ty = i / across * tileH;
                    budget -= tileRowBytes * tileH;
                    if (budget < 0) return Refused("it is damaged (tiles)");
                    var tile = Inflate(offsets[i], counts[i], compression, checked((int)(tileRowBytes * tileH)), out var whole);
                    if (!whole) return Refused(Cut);
                    if (predictor == 2) Undifference(tile, tileH, tileRowBytes, tileW, spp, bps);
                    var skip = (long)tx * spp * bps / 8;
                    var copy = Math.Min(tileRowBytes, rowBytes - skip);
                    for (var r = 0; r < tileH && ty + r < height; r++)
                        Array.Copy(tile, r * tileRowBytes, plane, (ty + r) * rowBytes + skip, copy);
                }
            }

            return new(Rgba(plane, width, height, rowBytes, spp, bps, photometric, t), width, height, pages, null);
        }

        /// <summary>Why a file whose image data stops short is refused: drawn, its missing part would show black.</summary>
        private const string Cut = "it is cut: part of its image data is missing";

        /// <summary>The most bytes one page's samples may take, and the longest side of a tile.</summary>
        private const long MaxPlaneBytes = 256L * 1024 * 1024;
        private const int MaxTileSide = 8192;

        /// <summary>One strip or tile — <paramref name="count"/> bytes at <paramref name="offset"/> — decompressed into exactly
        /// <paramref name="size"/> bytes; <paramref name="whole"/> is false when its data stops short of them.</summary>
        private byte[] Inflate(long offset, long count, int compression, int size, out bool whole)
        {
            if (offset < 0 || offset >= b.Length) throw new InvalidDataException("image data outside the file");
            var length = (int)Math.Min(count, b.Length - offset);
            var data = b.AsSpan((int)offset, length);
            var output = new byte[size];
            int filled;
            switch (compression)
            {
                case 1:
                    filled = Math.Min(data.Length, size);
                    data[..filled].CopyTo(output);
                    break;
                case 32773:
                    filled = PackBits(data, output);
                    break;
                case 5:
                    filled = Lzw(data, output);
                    break;
                default:                                         // 8, 32946: zlib
                    using (var z = new ZLibStream(new MemoryStream(b, (int)offset, length, writable: false), CompressionMode.Decompress))
                    {
                        filled = 0;
                        int read;
                        while (filled < size && (read = z.Read(output, filled, size - filled)) > 0) filled += read;
                    }
                    break;
            }
            whole = filled == size;
            return output;
        }

        private static int PackBits(ReadOnlySpan<byte> data, byte[] output)
        {
            int i = 0, o = 0;
            while (i < data.Length && o < output.Length)
            {
                var n = (sbyte)data[i++];
                if (n >= 0)
                {
                    var take = Math.Min(Math.Min(n + 1, data.Length - i), output.Length - o);
                    data.Slice(i, take).CopyTo(output.AsSpan(o));
                    i += n + 1;
                    o += take;
                }
                else if (n != -128 && i < data.Length)
                {
                    var take = Math.Min(1 - n, output.Length - o);
                    output.AsSpan(o, take).Fill(data[i++]);
                    o += take;
                }
            }
            return o;
        }

        /// <summary>TIFF's LZW: codes of 9 to 12 bits, most significant bit first, the width growing one code early.</summary>
        private static int Lzw(ReadOnlySpan<byte> data, byte[] output)
        {
            var table = new byte[4096][];
            for (var i = 0; i < 256; i++) table[i] = [(byte)i];
            int next = 258, width = 9, o = 0;
            long bit = 0;
            byte[]? previous = null;
            while (o < output.Length)
            {
                if (bit + width > data.Length * 8L) break;
                var code = 0;
                for (var k = 0; k < width; k++, bit++)
                    code = (code << 1) | ((data[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1);
                if (code == 257) break;                          // end of information
                if (code == 256) { next = 258; width = 9; previous = null; continue; }
                byte[] entry;
                if (code < next && table[code] is { } known) entry = known;
                else if (code == next && previous is not null) entry = [.. previous, previous[0]];
                else throw new InvalidDataException("LZW code out of its table");
                var take = Math.Min(entry.Length, output.Length - o);
                entry.AsSpan(0, take).CopyTo(output.AsSpan(o));
                o += take;
                if (previous is not null && next < 4096) table[next++] = [.. previous, entry[0]];
                previous = entry;
                if (next >= (1 << width) - 1 && width < 12) width++;
            }
            return o;
        }

        /// <summary>Undoes the horizontal predictor: each sample was written as its difference with the one before it.</summary>
        private void Undifference(byte[] block, int rows, long rowBytes, int width, int spp, int bps)
        {
            for (var r = 0; r < rows; r++)
            {
                var row = r * rowBytes;
                if (bps == 8)
                {
                    for (var i = spp; i < width * spp; i++) block[row + i] += block[row + i - spp];
                }
                else if (bps == 16)
                {
                    for (var i = spp; i < width * spp; i++)
                    {
                        var at = (int)(row + i * 2);
                        var value = (ushort)(Sample16(block, at) + Sample16(block, at - spp * 2));
                        if (little) BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(at), value);
                        else BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(at), value);
                    }
                }
            }
        }

        private ushort Sample16(byte[] a, int at) =>
            little ? BinaryPrimitives.ReadUInt16LittleEndian(a.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(at));

        /// <summary>The pixels as RGBA, 8 bits a channel.</summary>
        private byte[] Rgba(byte[] plane, int width, int height, long rowBytes, int spp, int bps, int photometric,
                            Dictionary<int, long[]> t)
        {
            var rgba = new byte[(long)width * height * 4];
            var map = t.GetValueOrDefault(320);
            var max = (1 << bps) - 1;
            // An extra sample is alpha only when the file says so (ExtraSamples 1, associated, or 2): an unspecified one is
            // another channel, drawn as alpha it would make the picture transparent.
            var extra = t.TryGetValue(338, out var kinds) && kinds.Length > 0 ? kinds[0] : 0;
            var alpha = extra is 1 or 2 && (photometric is 0 or 1 ? spp >= 2 : photometric == 2 ? spp >= 4 : photometric == 5 && spp >= 5);
            var premultiplied = alpha && extra == 1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    // Each sample scaled to 8 bits.
                    int S(int i)
                    {
                        long index = (long)x * spp + i;
                        if (bps == 8) return plane[y * rowBytes + index];
                        if (bps == 16) return Sample16(plane, (int)(y * rowBytes + index * 2)) >> 8;
                        var bitAt = index * bps;
                        var value = (plane[y * rowBytes + bitAt / 8] >> (int)(8 - bps - bitAt % 8)) & max;
                        return photometric == 3 ? value : value * 255 / max;
                    }
                    int r, g, bl, a = 255;
                    switch (photometric)
                    {
                        case 0: r = g = bl = 255 - S(0); if (alpha) a = S(1); break;
                        case 1: r = g = bl = S(0); if (alpha) a = S(1); break;
                        case 2: r = S(0); g = S(spp > 1 ? 1 : 0); bl = S(spp > 2 ? 2 : 0); if (alpha) a = S(3); break;
                        case 3:
                        {
                            var index = S(0);
                            var colours = 1 << bps;
                            r = (int)(map![index] >> 8); g = (int)(map[colours + index] >> 8); bl = (int)(map[2 * colours + index] >> 8);
                            break;
                        }
                        default:                                 // 5: CMYK
                        {
                            var k = S(3);
                            r = (255 - S(0)) * (255 - k) / 255; g = (255 - S(1)) * (255 - k) / 255; bl = (255 - S(2)) * (255 - k) / 255;
                            if (alpha) a = S(4);
                            break;
                        }
                    }
                    if (premultiplied && a is > 0 and < 255)
                    {
                        r = Math.Min(255, r * 255 / a); g = Math.Min(255, g * 255 / a); bl = Math.Min(255, bl * 255 / a);
                    }
                    var o = ((long)y * width + x) * 4;
                    rgba[o] = (byte)r; rgba[o + 1] = (byte)g; rgba[o + 2] = (byte)bl; rgba[o + 3] = (byte)a;
                }
            }
            return rgba;
        }
    }
}
