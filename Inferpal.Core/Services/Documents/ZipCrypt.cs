using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Inferpal.Services.Documents;

/// <summary>What reading a password-protected zip entry gave.</summary>
internal enum ZipCryptOutcome { Opened, WrongPassword, Unsupported, TooLarge, Damaged, NotFound }

/// <summary>
/// Reads one entry of a password-protected zip: the traditional PKWARE encryption (ZipCrypto) and WinZip's AES (AE-1, AE-2,
/// 128 to 256 bits), stored or deflated. Read-only, in memory, no library: the central directory, the entry's local header,
/// then its bytes decrypted and inflated within <see cref="Archives.MaxEntryBytes"/>.
/// </summary>
/// <remarks>
/// ⚠ A wrong password is told apart before anything is trusted: ZipCrypto's header check byte, then the CRC-32 of what it
/// decrypted (a wrong password passes the check byte once in 256); AES's password verifier, then the HMAC of the encrypted
/// bytes, checked BEFORE they are inflated. ⚠ The archive reader of .NET does not decrypt: the entry is found again here,
/// by its name in the central directory.
/// </remarks>
internal static class ZipCrypt
{
    /// <summary>Whether <paramref name="entry"/> of the zip at <paramref name="archive"/> is encrypted; false when it is not
    /// found or the archive cannot be read (its plain reading then says why).</summary>
    internal static bool IsEncrypted(string archive, string entry) => Find(archive, entry) is { Flags: var f } && (f & 1) != 0;

    /// <summary>
    /// Why <paramref name="entry"/> cannot be opened whatever the password — a compression or an encryption this reader
    /// does not decode, or a size past the limit —; <c>null</c> when a password can open it. Read from the directory only.
    /// </summary>
    /// <remarks>⚠ Asked before the card: otherwise the right password would be called wrong three times.</remarks>
    internal static string? CannotOpen(string archive, string entry)
    {
        if (Find(archive, entry) is not { } e) return "it could not be located for decryption";
        if ((e.Flags & 0x40) != 0) return "it uses PKWARE's strong encryption, which Inferpal does not read";
        var method = e.Method == 99 ? e.AesMethod : e.Method;
        if (e.Method == 99 && e.AesStrength is not (1 or 2 or 3)) return "its AES header is not one Inferpal reads";
        if (method is not (0 or 8)) return $"it is compressed with a method Inferpal does not read ({MethodName(method)}); extract it with an archive tool";
        if (e.CompressedSize > Archives.MaxEntryBytes + 64 * 1024 || e.UncompressedSize > Archives.MaxEntryBytes)
            return $"it is larger than {Archives.MaxEntryBytes / (1024 * 1024)} MB once read out";
        return null;
    }

    private static string MethodName(ushort m) => m switch { 12 => "BZip2", 14 => "LZMA", 93 => "Zstandard", 95 => "XZ", 98 => "PPMd", _ => $"method {m}" };

    /// <summary>The bytes a password is tried as: UTF-8 (current tools), the system's OEM code page (7-Zip and Windows'
    /// own zip folders), Latin-1 (older tools) — each once.</summary>
    private static IEnumerable<byte[]> Spellings(string password)
    {
        var seen = new List<byte[]>();
        foreach (var encoding in Encodings())
        {
            byte[] bytes;
            try { bytes = encoding.GetBytes(password); }
            catch (Exception ex) when (ex is ArgumentException or EncoderFallbackException) { continue; }
            if (seen.Any(s => s.AsSpan().SequenceEqual(bytes))) continue;
            seen.Add(bytes);
            yield return bytes;
        }
    }

    private static IEnumerable<Encoding> Encodings()
    {
        yield return Encoding.UTF8;
        Encoding? oem = null;
        try { oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        if (oem is not null) yield return oem;
        yield return Encoding.Latin1;
    }

    /// <summary>The plain bytes of <paramref name="entry"/>, opened with <paramref name="password"/>.</summary>
    internal static (byte[]? Bytes, ZipCryptOutcome Outcome) Read(string archive, string entry, string password, CancellationToken ct)
    {
        if (password.Length == 0) return (null, ZipCryptOutcome.WrongPassword);
        try
        {
            if (Find(archive, entry) is not { } e) return (null, ZipCryptOutcome.NotFound);
            if (e.CompressedSize > Archives.MaxEntryBytes + 64 * 1024) return (null, ZipCryptOutcome.TooLarge);
            using var f = File.OpenRead(archive);
            var local = new byte[30];
            f.Seek(e.LocalOffset, SeekOrigin.Begin);
            f.ReadExactly(local);
            if (BinaryPrimitives.ReadUInt32LittleEndian(local) != 0x04034b50) return (null, ZipCryptOutcome.Damaged);
            var skip = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28));
            f.Seek(skip, SeekOrigin.Current);
            var data = new byte[e.CompressedSize];
            f.ReadExactly(data);
            ct.ThrowIfCancellationRequested();

            // UTF-8 first (what current tools write), then Latin-1 (what older ones wrote, byte for byte).
            // Each spelling of the password is tried to the end — decrypted, inflated, its CRC checked: ZipCrypto's check
            // byte lets a wrong spelling through once in 256, and the right one must still get its turn.
            var last = ZipCryptOutcome.WrongPassword;
            foreach (var bytes in Spellings(password))
            {
                var (plain, outcome) = e.Method == 99 ? Aes(e, data, bytes) : Pkware(e, data, bytes);
                if (plain is not null)
                {
                    (byte[]? Bytes, ZipCryptOutcome Outcome) inflatedResult;
                    try { inflatedResult = Inflate(plain, e.Method == 99 ? e.AesMethod : e.Method, e.Method == 99 ? null : e.Crc, ct); }
                    catch (InvalidDataException) when (e.Method != 99) { continue; }   // garbage from a wrong spelling
                    var (opened, inflated) = inflatedResult;
                    if (inflated == ZipCryptOutcome.Opened) return (opened, inflated);
                    // An AES password proven by its HMAC that still does not inflate: the entry is damaged, not the password.
                    if (e.Method == 99) return (null, inflated);
                    continue;
                }
                if (outcome != ZipCryptOutcome.WrongPassword) last = outcome;
            }
            return (null, last);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
                                       or OverflowException or CryptographicException)
        {
            return (null, ZipCryptOutcome.Damaged);
        }
    }

    private sealed record Entry(ushort Flags, ushort Method, ushort Time, uint Crc, long CompressedSize, long UncompressedSize,
                                long LocalOffset, int AesStrength, ushort AesMethod);

    /// <summary>The entry named <paramref name="name"/> in the central directory, or <c>null</c>.</summary>
    private static Entry? Find(string archive, string name)
    {
        try
        {
            using var f = File.OpenRead(archive);
            var tail = (int)Math.Min(f.Length, 66_000);
            var buf = new byte[tail];
            f.Seek(-tail, SeekOrigin.End);
            f.ReadExactly(buf);
            var eocd = -1;
            for (var i = tail - 22; i >= 0; i--)
                if (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i)) == 0x06054b50) { eocd = i; break; }
            if (eocd < 0) return null;
            long count = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(eocd + 10));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(eocd + 12));
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(eocd + 16));
            if ((count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF) && eocd >= 20
                && BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(eocd - 20)) == 0x07064b50)
            {
                var rec = new byte[56];
                f.Seek((long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(eocd - 12)), SeekOrigin.Begin);
                f.ReadExactly(rec);
                count = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(32));
                size = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(40));
                offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(48));
            }
            if (count > Archives.MaxDeclaredEntries || size > 64L * 1024 * 1024 || offset + size > f.Length) return null;
            var dir = new byte[size];
            f.Seek(offset, SeekOrigin.Begin);
            f.ReadExactly(dir);
            for (var p = 0; p + 46 <= dir.Length && BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(p)) == 0x02014b50;)
            {
                var s = dir.AsSpan(p);
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(s[8..]);
                var method = BinaryPrimitives.ReadUInt16LittleEndian(s[10..]);
                var time = BinaryPrimitives.ReadUInt16LittleEndian(s[12..]);
                var crc = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]);
                long csize = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]);
                long usize = BinaryPrimitives.ReadUInt32LittleEndian(s[24..]);
                int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]);
                int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(s[30..]);
                int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(s[32..]);
                long local = BinaryPrimitives.ReadUInt32LittleEndian(s[42..]);
                if (p + 46 + nameLen + extraLen + commentLen > dir.Length) return null;
                var raw = s.Slice(46, nameLen);
                // UTF-8 first, as the listing (the .NET reader) decodes names; Latin-1 only for an old archive's name.
                if (Archives.Safe(Encoding.UTF8.GetString(raw)) == name || (flags & 0x800) == 0 && Archives.Safe(Encoding.Latin1.GetString(raw)) == name)
                {
                    int strength = 0; ushort aesMethod = 0;
                    var extra = s.Slice(46 + nameLen, extraLen);
                    for (var x = 0; x + 4 <= extra.Length;)
                    {
                        var id = BinaryPrimitives.ReadUInt16LittleEndian(extra[x..]);
                        var len = BinaryPrimitives.ReadUInt16LittleEndian(extra[(x + 2)..]);
                        var body = extra.Slice(x + 4, Math.Min(len, extra.Length - x - 4));
                        if (id == 0x0001)
                        {
                            var at = 0;
                            if (usize == 0xFFFFFFFF && at + 8 <= body.Length) { usize = (long)BinaryPrimitives.ReadUInt64LittleEndian(body[at..]); at += 8; }
                            if (csize == 0xFFFFFFFF && at + 8 <= body.Length) { csize = (long)BinaryPrimitives.ReadUInt64LittleEndian(body[at..]); at += 8; }
                            if (local == 0xFFFFFFFF && at + 8 <= body.Length) local = (long)BinaryPrimitives.ReadUInt64LittleEndian(body[at..]);
                        }
                        else if (id == 0x9901 && body.Length >= 7)
                        {
                            strength = body[4];
                            aesMethod = BinaryPrimitives.ReadUInt16LittleEndian(body[5..]);
                        }
                        x += 4 + len;
                    }
                    if (csize < 0 || local < 0 || local + csize > f.Length) return null;
                    return new Entry(flags, method, time, crc, csize, usize, local, strength, aesMethod);
                }
                p += 46 + nameLen + extraLen + commentLen;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException) { }
        return null;
    }

    // ── ZipCrypto (traditional PKWARE) ────────────────────────────────────────────────────────────────────────────

    private static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint CrcByte(uint crc, byte b) => Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data) crc = CrcByte(crc, b);
        return ~crc;
    }

    private static (byte[]? Plain, ZipCryptOutcome Outcome) Pkware(Entry e, byte[] data, byte[] password)
    {
        if (data.Length < 12) return (null, ZipCryptOutcome.Damaged);
        uint k0 = 0x12345678, k1 = 0x23456789, k2 = 0x34567890;
        void Update(byte c)
        {
            k0 = CrcByte(k0, c);
            k1 = unchecked((k1 + (k0 & 0xFF)) * 134775813 + 1);
            k2 = CrcByte(k2, (byte)(k1 >> 24));
        }
        byte Next() { var t = (k2 | 2) & 0xFFFF; return (byte)((t * (t ^ 1)) >> 8); }
        foreach (var b in password) Update(b);
        var header = new byte[12];
        for (var i = 0; i < 12; i++) { header[i] = (byte)(data[i] ^ Next()); Update(header[i]); }
        // The check byte: the CRC's high byte, or the modification time's when the sizes follow the data (bit 3).
        var check = (e.Flags & 0x8) != 0 ? (byte)(e.Time >> 8) : (byte)(e.Crc >> 24);
        if (header[11] != check) return (null, ZipCryptOutcome.WrongPassword);
        var plain = new byte[data.Length - 12];
        for (var i = 0; i < plain.Length; i++) { plain[i] = (byte)(data[12 + i] ^ Next()); Update(plain[i]); }
        return (plain, ZipCryptOutcome.Opened);
    }

    // ── WinZip AES (AE-1, AE-2) ──────────────────────────────────────────────────────────────────────────────────

    private static (byte[]? Plain, ZipCryptOutcome Outcome) Aes(Entry e, byte[] data, byte[] password)
    {
        var keyLength = e.AesStrength switch { 1 => 16, 2 => 24, 3 => 32, _ => 0 };
        if (keyLength == 0) return (null, ZipCryptOutcome.Unsupported);
        var saltLength = keyLength / 2;
        if (data.Length < saltLength + 2 + 10) return (null, ZipCryptOutcome.Damaged);
        var salt = data.AsSpan(0, saltLength);
        var derived = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1000, HashAlgorithmName.SHA1, 2 * keyLength + 2);
        if (!derived.AsSpan(2 * keyLength, 2).SequenceEqual(data.AsSpan(saltLength, 2))) return (null, ZipCryptOutcome.WrongPassword);
        var cipher = data.AsSpan(saltLength + 2, data.Length - saltLength - 2 - 10);
        var mac = HMACSHA1.HashData(derived.AsSpan(keyLength, keyLength), cipher);
        // Checked before anything is decrypted or inflated: a damaged or forged entry stops here.
        if (!CryptographicOperations.FixedTimeEquals(mac.AsSpan(0, 10), data.AsSpan(data.Length - 10)))
            return (null, ZipCryptOutcome.Damaged);
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = derived[..keyLength];
        var plain = new byte[cipher.Length];
        var counter = new byte[16];
        var stream = new byte[16];
        for (var at = 0; at < cipher.Length; at += 16)
        {
            // A little-endian block counter, from 1.
            for (var i = 0; i < 16 && ++counter[i] == 0; i++) { }
            aes.EncryptEcb(counter, stream, PaddingMode.None);
            var n = Math.Min(16, cipher.Length - at);
            for (var i = 0; i < n; i++) plain[at + i] = (byte)(cipher[at + i] ^ stream[i]);
        }
        return (plain, ZipCryptOutcome.Opened);
    }

    private static (byte[]? Bytes, ZipCryptOutcome Outcome) Inflate(byte[] plain, ushort method, uint? crc, CancellationToken ct)
    {
        byte[] output;
        if (method == 0) output = plain;
        else if (method == 8)
        {
            using var inflater = new DeflateStream(new MemoryStream(plain), CompressionMode.Decompress);
            using var copy = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (copy.Length + read > Archives.MaxEntryBytes) return (null, ZipCryptOutcome.TooLarge);
                copy.Write(buffer, 0, read);
            }
            output = copy.ToArray();
        }
        else return (null, ZipCryptOutcome.Unsupported);
        if (output.Length > Archives.MaxEntryBytes) return (null, ZipCryptOutcome.TooLarge);
        // ZipCrypto's check byte lets one wrong password in 256 through: the CRC of what it opened decides.
        if (crc is { } expected && Crc32(output) != expected) return (null, ZipCryptOutcome.WrongPassword);
        return (output, ZipCryptOutcome.Opened);
    }
}
