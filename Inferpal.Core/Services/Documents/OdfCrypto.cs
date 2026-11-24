using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Konscious.Security.Cryptography;

namespace Inferpal.Services.Documents;

/// <summary>
/// An OpenDocument file protected by a password, as its manifest declares it, and opened with the password the person
/// typed. Two forms: LibreOffice 24.2 and later encrypt the whole document as one part, <c>encrypted-package</c> (AES-256-GCM,
/// key by Argon2id); earlier versions encrypt each part (AES-256-CBC, key by PBKDF2, a checksum to tell a wrong password).
/// Either way the key is derived from the SHA-256 of the password.
/// </summary>
internal static class OdfCrypto
{
    private const string ManifestNs = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private const string LoExt = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";
    /// <summary>Argon2 memory a file may ask for at most, in KiB: LibreOffice asks 64 MiB; more would let a file take the
    /// editor's memory.</summary>
    private const int MaxArgon2MemoryKiB = 256 * 1024;
    /// <summary>The most a file may ask, per derivation: LibreOffice writes 100 000 PBKDF2 rounds, 3 Argon2 passes. Neither
    /// call can be interrupted, so a file asking millions would hold the reader for hours once a password is typed.</summary>
    private const int MaxPbkdf2Iterations = 1_000_000;
    private const int MaxArgon2Iterations = 64;

    /// <summary>How a part is encrypted, from its <c>manifest:encryption-data</c>.</summary>
    internal sealed record Encryption(string Path, string Algorithm, byte[] Iv, string StartKey, string Derivation, byte[] Salt,
                                      int KeySize, int Iterations, int MemoryKiB, int Lanes, string? ChecksumType, byte[]? Checksum);

    /// <summary>What a password opened: the whole package (a zip), or the content part.</summary>
    internal abstract record Opened
    {
        internal sealed record Package(byte[] Bytes) : Opened;
        internal sealed record Content(byte[] Bytes) : Opened;
    }

    /// <summary>The encryption of the part the text is read from; null when the document is not encrypted.</summary>
    internal static Encryption? Protection(ZipArchive zip)
    {
        var manifest = zip.GetEntry("META-INF/manifest.xml");
        if (manifest is null || manifest.Length > 4 * 1024 * 1024) return null;
        var document = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(manifest.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            document.Load(reader);
        var namespaces = new XmlNamespaceManager(document.NameTable);
        namespaces.AddNamespace("m", ManifestNs);
        Encryption? found = null;
        foreach (XmlElement entry in document.SelectNodes("//m:file-entry[m:encryption-data]", namespaces)!)
        {
            var path = entry.GetAttribute("full-path", ManifestNs);
            var data = (XmlElement)entry.SelectSingleNode("m:encryption-data", namespaces)!;
            var algorithm = (XmlElement?)data.SelectSingleNode("m:algorithm", namespaces);
            var start = (XmlElement?)data.SelectSingleNode("m:start-key-generation", namespaces);
            var derivation = (XmlElement?)data.SelectSingleNode("m:key-derivation", namespaces);
            if (algorithm is null || derivation is null) continue;
            int Int(XmlElement e, string name, string ns) =>
                int.TryParse(e.GetAttribute(name, ns), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            var checksum = data.GetAttribute("checksum", ManifestNs);
            var encryption = new Encryption(path,
                algorithm.GetAttribute("algorithm-name", ManifestNs),
                Convert.FromBase64String(algorithm.GetAttribute("initialisation-vector", ManifestNs)),
                start?.GetAttribute("start-key-generation-name", ManifestNs) ?? "SHA1",
                derivation.GetAttribute("key-derivation-name", ManifestNs),
                Convert.FromBase64String(derivation.GetAttribute("salt", ManifestNs)),
                Int(derivation, "key-size", ManifestNs) is > 0 and var size ? size : 16,
                Math.Max(Int(derivation, "iteration-count", ManifestNs), Int(derivation, "argon2-iterations", LoExt)),
                Int(derivation, "argon2-memory", LoExt), Int(derivation, "argon2-lanes", LoExt),
                data.GetAttribute("checksum-type", ManifestNs) is { Length: > 0 } t ? t : null,
                checksum.Length > 0 ? Convert.FromBase64String(checksum) : null);
            // The part the text comes from: the whole package, else content.xml.
            if (path == "encrypted-package") return encryption;
            if (path == "content.xml") found = encryption;
            found ??= encryption;
        }
        return found;
    }

    /// <summary>The part, decrypted and inflated; null when the password does not open it or the encryption is not one this
    /// reader knows (Blowfish, of OpenOffice 2, is not opened).</summary>
    internal static Opened? Open(ZipArchive zip, Encryption encryption, string password, CancellationToken ct)
    {
        var entry = zip.GetEntry(encryption.Path);
        if (entry is null) return null;
        if (entry.Length > DocumentText.MaxBytes) throw new DocumentLimitException("An encrypted OpenDocument part past its size limit");
        var data = new byte[entry.Length];
        using (var stream = entry.Open()) stream.ReadExactly(data);

        var startKey = encryption.StartKey.EndsWith("sha256", StringComparison.OrdinalIgnoreCase)
            ? SHA256.HashData(Encoding.UTF8.GetBytes(password))
            : SHA1.HashData(Encoding.UTF8.GetBytes(password));
        var key = Key(encryption, startKey, ct);
        if (key is null) return null;

        byte[] compressed;
        if (encryption.Algorithm.EndsWith("aes256-gcm", StringComparison.Ordinal))
        {
            // XML Encryption 1.1: the part carries its 12-byte IV first and its 16-byte tag last (the manifest repeats the IV).
            if (data.Length < 28) return null;
            compressed = new byte[data.Length - 28];
            try
            {
                using var gcm = new AesGcm(key, 16);
                gcm.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, data.Length - 28), data.AsSpan(data.Length - 16), compressed);
            }
            catch (AuthenticationTagMismatchException) { return null; }   // a wrong password
        }
        else if (encryption.Algorithm.EndsWith("aes256-cbc", StringComparison.Ordinal))
        {
            using var aes = Aes.Create();
            aes.Key = key;
            byte[] decrypted;
            try { decrypted = aes.DecryptCbc(data, encryption.Iv, PaddingMode.ISO10126); }
            catch (CryptographicException) { return null; }
            // A wrong password is told by the checksum of the first kilobyte, which the padding check alone misses.
            if (encryption.Checksum is { } sum)
            {
                var head = decrypted.AsSpan(0, Math.Min(1024, decrypted.Length));
                var mine = encryption.ChecksumType?.Contains("sha256", StringComparison.OrdinalIgnoreCase) == true
                    ? SHA256.HashData(head) : SHA1.HashData(head);
                if (!CryptographicOperations.FixedTimeEquals(mine, sum)) return null;
            }
            compressed = decrypted;
        }
        else return null;

        var plain = Inflate(compressed, ct);
        return encryption.Path == "encrypted-package" ? new Opened.Package(plain) : new Opened.Content(plain);
    }

    private static byte[]? Key(Encryption encryption, byte[] startKey, CancellationToken ct)
    {
        // AES-128 or AES-256: any other key size is no key these ciphers take (a file asking 2 GB of key is refused).
        if (encryption.Iterations <= 0 || encryption.KeySize is not (16 or 32)) return null;
        if (encryption.Derivation.EndsWith("argon2id", StringComparison.Ordinal))
        {
            if (encryption.Iterations > MaxArgon2Iterations || encryption.MemoryKiB is <= 0 or > MaxArgon2MemoryKiB
                || encryption.Lanes is <= 0 or > 64) return null;
            ct.ThrowIfCancellationRequested();
            using var argon = new Argon2id(startKey)
            {
                Salt = encryption.Salt, Iterations = encryption.Iterations, MemorySize = encryption.MemoryKiB,
                DegreeOfParallelism = encryption.Lanes,
            };
            return argon.GetBytes(encryption.KeySize);
        }
        if (encryption.Derivation.EndsWith("PBKDF2", StringComparison.OrdinalIgnoreCase) && encryption.Iterations <= MaxPbkdf2Iterations)
            return Rfc2898DeriveBytes.Pbkdf2(startKey, encryption.Salt, encryption.Iterations, HashAlgorithmName.SHA1, encryption.KeySize);
        return null;
    }

    /// <summary>A part is deflated before it is encrypted; a package already a zip is returned as it is.</summary>
    internal static byte[] Inflate(byte[] compressed, CancellationToken ct)
    {
        if (compressed.Length >= 2 && compressed[0] == (byte)'P' && compressed[1] == (byte)'K') return compressed;
        using var input = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            if (output.Length > SpreadsheetText.MaxInflatedBytes) throw new DocumentLimitException("An OpenDocument part inflating past its limit");
        }
        return output.ToArray();
    }
}
