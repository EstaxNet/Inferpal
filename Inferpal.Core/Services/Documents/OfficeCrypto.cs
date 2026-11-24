using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Inferpal.Services.Documents;

/// <summary>
/// An Office document protected by a password to open: an OLE compound file (read by OpenMcdf) holding <c>EncryptionInfo</c> and
/// <c>EncryptedPackage</c> (MS-OFFCRYPTO). Opens the agile encryption every Office since 2010 writes, and the standard one of
/// Office 2007 and LibreOffice (<see cref="Standard"/>) — for agile, the password hashed
/// with its salt, spun, then the package decrypted in 4 096-byte segments — and gives back the zip inside.
/// </summary>
internal static class OfficeCrypto
{
    /// <summary>The compound file's signature (D0 CF 11 E0 A1 B1 1A E1).</summary>
    internal static bool IsCompoundFile(byte[] bytes) =>
        bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });

    /// <summary>A compound file that holds an encrypted Office package (as opposed to an .xls, a .doc, a .msg).</summary>
    internal static bool IsEncryptedPackage(byte[] bytes)
    {
        try
        {
            using var root = OpenMcdf.RootStorage.Open(DocText.Expandable(bytes));
            return root.EnumerateEntries().Any(e => e.Type == OpenMcdf.EntryType.Stream && e.Name == "EncryptedPackage");
        }
        // OpenMcdf is stricter than the extractors: a compound file it refuses (one ExcelDataReader reads) is no package,
        // and its reader goes on — never refused here.
        catch (Exception ex) when (ex is IOException or FormatException or ArgumentException or NotSupportedException
                                       or InvalidDataException or InvalidOperationException)
        {
            Diagnostics.Swallow("OfficeCrypto.IsEncryptedPackage", ex);
            return false;
        }
    }

    /// <summary>The spin count a file may ask for at most: Office writes 100 000; a file asking far more stalls the reader on
    /// a hash it cannot interrupt.</summary>
    private const int MaxSpinCount = 1_000_000;

    private static readonly byte[] VerifierInputBlock = [0xfe, 0xa7, 0xd2, 0x76, 0x3b, 0x4b, 0x9e, 0x79];
    private static readonly byte[] VerifierValueBlock = [0xd7, 0xaa, 0x0f, 0x6d, 0x30, 0x61, 0x34, 0x4e];
    private static readonly byte[] KeyValueBlock      = [0x14, 0x6e, 0x0b, 0xe7, 0xab, 0xac, 0xd0, 0xd6];

    /// <summary>
    /// Whether this reader opens the encryption of <paramref name="file"/> at all — agile (4.4) or standard (3.2, 4.2) with
    /// AES — with the right password. ⚠ Another one (an RC4 CryptoAPI package, a .doc renamed .docx) is no card to ask:
    /// every right password would be called wrong three times, then the file left closed.
    /// </summary>
    internal static bool Opens(byte[] file)
    {
        try
        {
            using var root = OpenMcdf.RootStorage.Open(DocText.Expandable(file));
            if (Stream(root, "EncryptionInfo") is not { Length: >= 32 } info || Stream(root, "EncryptedPackage") is null) return false;
            var (major, minor) = (BitConverter.ToUInt16(info, 0), BitConverter.ToUInt16(info, 2));
            if (major == 4 && minor == 4) return true;
            return IsCryptoApiVersion(major, minor) && BitConverter.ToInt32(info, 12 + 8) is 0x660E or 0x660F or 0x6610;
        }
        catch (Exception ex) when (ex is IOException or FormatException or ArgumentException or NotSupportedException
                                       or InvalidDataException or InvalidOperationException)
        {
            Diagnostics.Swallow("OfficeCrypto.Opens", ex);
            return false;
        }
    }

    /// <summary>The versions a standard or RC4 CryptoAPI encryption header may carry: 2.2, 3.2 or 4.2 (MS-OFFCRYPTO).</summary>
    internal static bool IsCryptoApiVersion(ushort major, ushort minor) => major is >= 2 and <= 4 && minor == 2;

    /// <summary>
    /// The package (the zip) inside an encrypted container, opened with the password the person typed — or <c>null</c> and why
    /// it stays shut in <paramref name="shut"/>: an encryption this reader does not open is unreadable, never a password card
    /// no password can pass; no password, or a wrong one, is encrypted.
    /// </summary>
    internal static byte[]? Unlock(byte[] file, string? password, CancellationToken ct, out DocumentProblem shut)
    {
        shut = !Opens(file) ? DocumentProblem.Unreadable : DocumentProblem.Encrypted;
        return shut == DocumentProblem.Encrypted && password is not null ? Decrypt(file, password, ct) : null;
    }

    /// <summary>The package (the zip) inside, decrypted with <paramref name="password"/>; null when the password does not
    /// open it, or the encryption is not one this reader opens.</summary>
    internal static byte[]? Decrypt(byte[] file, string password, CancellationToken ct)
    {
        using var root = OpenMcdf.RootStorage.Open(DocText.Expandable(file));
        var info = Stream(root, "EncryptionInfo");
        var package = Stream(root, "EncryptedPackage");
        if (info is null || package is null || info.Length < 8 || package.Length < 8) return null;
        var (major, minor) = (BitConverter.ToUInt16(info, 0), BitConverter.ToUInt16(info, 2));
        if (IsCryptoApiVersion(major, minor)) return Standard(info, package, password, ct);   // Office 2007, and LibreOffice today
        if (major != 4 || minor != 4) return null;   // agile is version 4.4
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

    /// <summary>
    /// Standard encryption (MS-OFFCRYPTO 2.3.4.5-2.3.4.9): Office 2007 writes it, and LibreOffice when it saves a protected
    /// .docx, .xlsx or .pptx. AES in ECB mode, its key from SHA-1 spun 50 000 times over the salt and the password.
    /// </summary>
    private static byte[]? Standard(byte[] info, byte[] package, string password, CancellationToken ct)
    {
        var headerSize = BitConverter.ToInt32(info, 8);
        if (headerSize < 32 || 12 + headerSize + 4 + 16 + 16 + 4 + 32 > info.Length) return null;
        var algorithm = BitConverter.ToInt32(info, 12 + 8);
        var keyBits = BitConverter.ToInt32(info, 12 + 16);
        if (algorithm is not (0x660E or 0x660F or 0x6610) || keyBits is not (128 or 192 or 256)) return null;   // AES only
        var verifier = 12 + headerSize;
        var salt = info.AsSpan(verifier + 4, 16).ToArray();

        var h = SHA1.HashData([.. salt, .. Encoding.Unicode.GetBytes(password)]);
        var iterator = new byte[4];
        for (var i = 0; i < 50_000; i++)
        {
            BitConverter.TryWriteBytes(iterator, i);
            h = SHA1.HashData([.. iterator, .. h]);
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
        }
        var final = SHA1.HashData([.. h, 0, 0, 0, 0]);
        byte[] Mixed(byte pad)
        {
            var buffer = Enumerable.Repeat(pad, 64).ToArray();
            for (var i = 0; i < final.Length; i++) buffer[i] ^= final[i];
            return SHA1.HashData(buffer);
        }
        var key = Mixed(0x36).Concat(Mixed(0x5C)).Take(keyBits / 8).ToArray();

        using var aes = Aes.Create();
        aes.Key = key;
        var checkedVerifier = aes.DecryptEcb(info.AsSpan(verifier + 20, 16), PaddingMode.None);
        var checkedHash = aes.DecryptEcb(info.AsSpan(verifier + 40, 32), PaddingMode.None);
        if (!CryptographicOperations.FixedTimeEquals(SHA1.HashData(checkedVerifier), checkedHash.AsSpan(0, 20))) return null;

        var size = BitConverter.ToInt64(package, 0);
        var body = package.AsSpan(8);
        if (size < 0 || size > body.Length) return null;
        var plain = aes.DecryptEcb(body[..(body.Length - body.Length % 16)], PaddingMode.None);
        return plain[..(int)size];
    }

    /// <summary>A stream of the container, whole; null when there is none. Bounded by the document limit.</summary>
    private static byte[]? Stream(OpenMcdf.RootStorage root, string name)
    {
        if (!root.EnumerateEntries().Any(e => e.Type == OpenMcdf.EntryType.Stream && e.Name == name)) return null;
        using var stream = root.OpenStream(name);
        if (stream.Length > DocumentText.MaxBytes) throw new DocumentLimitException("An Office container stream past its size limit");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
