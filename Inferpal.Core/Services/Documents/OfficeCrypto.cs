using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Inferpal.Services.Documents;

/// <summary>
/// A Word document protected by a password to open: an OLE compound file holding <c>EncryptionInfo</c> and
/// <c>EncryptedPackage</c> (MS-OFFCRYPTO). Opens the agile encryption every Office since 2010 writes — the password hashed
/// with its salt, spun, then the package decrypted in 4 096-byte segments — and gives back the zip inside.
/// </summary>
/// <remarks>shortcut: agile encryption only — a file from Office 2007 (standard encryption) stays locked; add algorithm
/// 2.3.4.7 if such files show up.</remarks>
internal static class OfficeCrypto
{
    /// <summary>The compound file's signature (D0 CF 11 E0 A1 B1 1A E1).</summary>
    internal static bool IsCompoundFile(byte[] bytes) =>
        bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });

    /// <summary>The spin count a file may ask for at most: Office writes 100 000; far more would stall the reader.</summary>
    private const int MaxSpinCount = 10_000_000;

    private static readonly byte[] VerifierInputBlock = [0xfe, 0xa7, 0xd2, 0x76, 0x3b, 0x4b, 0x9e, 0x79];
    private static readonly byte[] VerifierValueBlock = [0xd7, 0xaa, 0x0f, 0x6d, 0x30, 0x61, 0x34, 0x4e];
    private static readonly byte[] KeyValueBlock      = [0x14, 0x6e, 0x0b, 0xe7, 0xab, 0xac, 0xd0, 0xd6];

    /// <summary>The package (the zip) inside, decrypted with <paramref name="password"/>; null when the password does not
    /// open it, or the encryption is not one this reader opens.</summary>
    internal static byte[]? Decrypt(byte[] file, string password, CancellationToken ct)
    {
        var cfb = new CompoundFile(file);
        var info = cfb.Stream("EncryptionInfo");
        var package = cfb.Stream("EncryptedPackage");
        if (info is null || package is null || info.Length < 8 || package.Length < 8) return null;
        if (BitConverter.ToUInt16(info, 0) != 4 || BitConverter.ToUInt16(info, 2) != 4) return null;   // agile is version 4.4
        var xml = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new MemoryStream(info, 8, info.Length - 8),
                   new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            xml.Load(reader);
        var keyData = xml.GetElementsByTagName("keyData", "http://schemas.microsoft.com/office/2006/encryption")
                         .OfType<XmlElement>().FirstOrDefault();
        // The password's key encryptor; a file also locked to a certificate carries another one beside it.
        var encryptedKey = xml.GetElementsByTagName("encryptedKey", "http://schemas.microsoft.com/office/2006/keyEncryptor/password")
                              .OfType<XmlElement>().FirstOrDefault();
        if (keyData is null || encryptedKey is null) return null;
        if (encryptedKey.GetAttribute("cipherAlgorithm") is { Length: > 0 } c && c != "AES") return null;

        // The key that decrypts the package's key, from the password (2.3.4.11).
        var spin = int.Parse(encryptedKey.GetAttribute("spinCount"), System.Globalization.CultureInfo.InvariantCulture);
        if (spin < 0 || spin > MaxSpinCount) return null;
        var keyHash = HashOf(encryptedKey.GetAttribute("hashAlgorithm"));
        if (keyHash is null) return null;
        var salt = Convert.FromBase64String(encryptedKey.GetAttribute("saltValue"));
        var keyBytes = int.Parse(encryptedKey.GetAttribute("keyBits"), System.Globalization.CultureInfo.InvariantCulture) / 8;
        var h = keyHash([.. salt, .. Encoding.Unicode.GetBytes(password)]);
        var iterator = new byte[4];
        for (var i = 0; i < spin; i++)
        {
            BitConverter.TryWriteBytes(iterator, i);
            h = keyHash([.. iterator, .. h]);
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
        }
        byte[] KeyFor(byte[] block) => Fit(keyHash([.. h, .. block]), keyBytes, 0x36);
        byte[] Open(byte[] block, string attribute)
        {
            using var aes = Aes.Create();
            aes.Key = KeyFor(block);
            return aes.DecryptCbc(Convert.FromBase64String(encryptedKey.GetAttribute(attribute)), Fit(salt, 16, 0x36), PaddingMode.None);
        }

        // The password opens the file when the verifier's hash matches (2.3.4.13).
        var verifier = Open(VerifierInputBlock, "encryptedVerifierHashInput")[..salt.Length];
        var expected = Open(VerifierValueBlock, "encryptedVerifierHashValue");
        var hashed = keyHash(verifier);
        if (expected.Length < hashed.Length || !CryptographicOperations.FixedTimeEquals(hashed, expected.AsSpan(0, hashed.Length))) return null;
        var secret = Open(KeyValueBlock, "encryptedKeyValue")[..(int.Parse(keyData.GetAttribute("keyBits"), System.Globalization.CultureInfo.InvariantCulture) / 8)];

        // The package, segment by segment, each with its own IV (2.3.4.15).
        var dataHash = HashOf(keyData.GetAttribute("hashAlgorithm"));
        if (dataHash is null) return null;
        var dataSalt = Convert.FromBase64String(keyData.GetAttribute("saltValue"));
        var blockSize = int.Parse(keyData.GetAttribute("blockSize"), System.Globalization.CultureInfo.InvariantCulture);
        var size = BitConverter.ToInt64(package, 0);
        if (size < 0 || size > package.Length) return null;
        var output = new byte[size];
        using var cipher = Aes.Create();
        cipher.Key = secret;
        var segmentIndex = new byte[4];
        for (long at = 8, written = 0, segment = 0; written < size; at += 4096, segment++)
        {
            ct.ThrowIfCancellationRequested();
            var length = (int)Math.Min(4096, package.Length - at);
            length -= length % 16;
            if (length <= 0) return null;
            BitConverter.TryWriteBytes(segmentIndex, (int)segment);
            var plain = cipher.DecryptCbc(package.AsSpan((int)at, length), Fit(dataHash([.. dataSalt, .. segmentIndex]), blockSize, 0x36), PaddingMode.None);
            var take = (int)Math.Min(plain.Length, size - written);
            plain.AsSpan(0, take).CopyTo(output.AsSpan((int)written));
            written += take;
        }
        return output;
    }

    private static Func<byte[], byte[]>? HashOf(string name) => name switch
    {
        "SHA512" => SHA512.HashData,
        "SHA384" => SHA384.HashData,
        "SHA256" => SHA256.HashData,
        "SHA1"   => SHA1.HashData,
        _        => null,
    };

    /// <summary>Cut to <paramref name="length"/> bytes, or padded with <paramref name="pad"/> (2.3.4.12).</summary>
    private static byte[] Fit(byte[] bytes, int length, byte pad)
    {
        if (bytes.Length >= length) return bytes[..length];
        var fitted = Enumerable.Repeat(pad, length).ToArray();
        bytes.CopyTo(fitted, 0);
        return fitted;
    }

    /// <summary>The streams of an OLE compound file (MS-CFB), read by name from the root storage.</summary>
    private sealed class CompoundFile
    {
        private readonly byte[] _data;
        private readonly int _sector;
        private readonly int _miniSector;
        private readonly uint _miniCutoff;
        private readonly List<uint> _fat = [];
        private readonly List<uint> _miniFat = [];
        private readonly List<(string Name, int Type, uint Start, long Size)> _entries = [];
        private byte[] _miniStream = [];

        private const uint EndOfChain = 0xFFFFFFFE;

        public CompoundFile(byte[] data)
        {
            _data = data;
            if (data.Length < 512) throw new InvalidDataException("A compound file shorter than its header");
            _sector = 1 << BitConverter.ToUInt16(data, 0x1E);
            _miniSector = 1 << BitConverter.ToUInt16(data, 0x20);
            if (_sector is not (512 or 4096) || _miniSector != 64) throw new InvalidDataException("A compound file of an unknown sector size");
            _miniCutoff = BitConverter.ToUInt32(data, 0x38);
            // The FAT's sectors: the first 109 in the header, the rest chained through DIFAT sectors.
            var fatSectors = new List<uint>();
            for (var i = 0; i < 109; i++)
            {
                var s = BitConverter.ToUInt32(data, 0x4C + 4 * i);
                if (s < 0xFFFFFFFA) fatSectors.Add(s);
            }
            var difat = BitConverter.ToUInt32(data, 0x44);
            for (var guard = 0; difat < 0xFFFFFFFA && guard < 10_000; guard++)
            {
                var at = Offset(difat);
                for (var i = 0; i < _sector / 4 - 1; i++)
                {
                    var s = BitConverter.ToUInt32(data, at + 4 * i);
                    if (s < 0xFFFFFFFA) fatSectors.Add(s);
                }
                difat = BitConverter.ToUInt32(data, at + _sector - 4);
            }
            foreach (var s in fatSectors)
            {
                var at = Offset(s);
                for (var i = 0; i < _sector / 4; i++) _fat.Add(BitConverter.ToUInt32(data, at + 4 * i));
            }
            var directory = Chain(BitConverter.ToUInt32(data, 0x30), long.MaxValue);
            for (var at = 0; at + 128 <= directory.Length; at += 128)
            {
                var nameLength = Math.Clamp((int)BitConverter.ToUInt16(directory, at + 64), 2, 64);
                var name = Encoding.Unicode.GetString(directory, at, nameLength - 2);
                _entries.Add((name, directory[at + 66], BitConverter.ToUInt32(directory, at + 116), (long)BitConverter.ToUInt64(directory, at + 120)));
            }
            if (_entries.Count == 0 || _entries[0].Type != 5) throw new InvalidDataException("A compound file without its root");
            var miniFat = Chain(BitConverter.ToUInt32(data, 0x3C), long.MaxValue);
            for (var i = 0; i + 4 <= miniFat.Length; i += 4) _miniFat.Add(BitConverter.ToUInt32(miniFat, i));
            _miniStream = Chain(_entries[0].Start, _entries[0].Size);
        }

        private int Offset(uint sector)
        {
            var at = (long)(sector + 1) * _sector;
            if (at + _sector > _data.Length) throw new InvalidDataException("A compound file sector past its end");
            return (int)at;
        }

        /// <summary>A chain of regular sectors, at most <paramref name="size"/> bytes.</summary>
        private byte[] Chain(uint start, long size)
        {
            var output = new MemoryStream();
            for (var (s, guard) = (start, 0); s < 0xFFFFFFFA && output.Length < size; s = s < _fat.Count ? _fat[(int)s] : EndOfChain, guard++)
            {
                if (guard > _fat.Count) throw new InvalidDataException("A compound file chain that loops");
                output.Write(_data, Offset(s), _sector);
            }
            var bytes = output.ToArray();
            return size < bytes.Length ? bytes[..(int)size] : bytes;
        }

        /// <summary>A chain of mini sectors in the mini stream.</summary>
        private byte[] MiniChain(uint start, long size)
        {
            var output = new MemoryStream();
            for (var (s, guard) = (start, 0); s < 0xFFFFFFFA && output.Length < size; s = s < _miniFat.Count ? _miniFat[(int)s] : EndOfChain, guard++)
            {
                if (guard > _miniFat.Count) throw new InvalidDataException("A compound file mini chain that loops");
                var at = (long)s * _miniSector;
                if (at + _miniSector > _miniStream.Length) throw new InvalidDataException("A mini sector past the mini stream");
                output.Write(_miniStream, (int)at, _miniSector);
            }
            var bytes = output.ToArray();
            return size < bytes.Length ? bytes[..(int)size] : bytes;
        }

        /// <summary>The stream named <paramref name="name"/>; null when there is none.</summary>
        public byte[]? Stream(string name)
        {
            foreach (var e in _entries)
            {
                if (e.Type != 2 || e.Name != name) continue;
                if (e.Size > _data.Length) throw new InvalidDataException("A compound file stream larger than the file");
                return e.Size < _miniCutoff ? MiniChain(e.Start, e.Size) : Chain(e.Start, e.Size);
            }
            return null;
        }
    }
}
