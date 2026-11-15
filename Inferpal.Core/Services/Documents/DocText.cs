using System.Security.Cryptography;
using System.Text;
using b2xtranslator.DocFileFormat;
using b2xtranslator.StructuredStorage.Reader;
using b2xtranslator.txt;
using b2xtranslator.txt.TextModel;
using OpenMcdf;

namespace Inferpal.Services.Documents;

/// <summary>
/// The text of a Word 97-2003 document (<c>.doc</c>), read by b2xtranslator's text extractor (GHSoftware.WordDocTextExtractor,
/// BSD-3-Clause; measured on Apache POI's sample files). A document protected by a password is decrypted here first — the
/// library reads none — with the RC4 of Word 97 or the RC4 CryptoAPI of Word 2002-2003 (MS-OFFCRYPTO 2.3.6 and 2.3.5).
/// </summary>
/// <remarks>shortcut: Word 95's XOR obfuscation is not opened (said protected); add it if such files show up.</remarks>
internal static class DocText
{
    private const int FibClearBytes = 68;   // the first bytes of the FIB, written in clear in an encrypted document
    private const int BlockSize = 512;

    internal static DocumentExtraction Extract(byte[] bytes, CancellationToken ct, string? password)
    {
        try
        {
            if (EncryptionOrNull(bytes) is { } encrypted)
            {
                if (password is null || Decrypt(bytes, encrypted, password, ct) is not { } plain)
                    return new(string.Empty, 0, 0, DocumentProblem.Encrypted);
                bytes = plain;
            }
            ct.ThrowIfCancellationRequested();
            using var reader = new StructuredStorageReader(Expandable(bytes), null);
            var document = new WordDocument(reader);
            var text = DocTextExtractor.ConvertToString(document, TextDocument.Create("", null, false), extractUrls: false);
            ct.ThrowIfCancellationRequested();
            var read = System.Text.RegularExpressions.Regex.Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n{3,}", "\n\n").Trim();
            if (read.Length == 0) return new(string.Empty, 0, 0, DocumentProblem.NoText);
            return new(read.Length > PdfText.MaxChars ? read[..PdfText.MaxChars] : read, 0, 0, DocumentProblem.None,
                       read.Length > PdfText.MaxChars ? 1 : 0);
        }
        catch (Exception ex) when (ex.GetType().Name == "EncryptedFileException")
        {
            return new(string.Empty, 0, 0, DocumentProblem.Encrypted);   // the extractor saw the encryption itself
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DocumentLimitException
                                       and not OutOfMemoryException and not StackOverflowException)
        {
            // The library throws its own types on a damaged file (a sector chain that loops, a missing magic number):
            // said unreadable, never a failed tool.
            Diagnostics.Swallow("DocText.Extract", ex);
            return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
        }
    }

    private sealed record EncryptionInfo(string Table, byte[] Header, bool Obfuscated);

    /// <summary>The document's encryption; null when it is not encrypted, or when OpenMcdf — stricter than the extractor —
    /// cannot open the container: the extractor then judges, and says an encrypted file is so itself.</summary>
    private static EncryptionInfo? EncryptionOrNull(byte[] bytes)
    {
        try { return Encryption(bytes); }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Diagnostics.Swallow("DocText.Encryption", ex);
            return null;
        }
    }

    /// <summary>The document's encryption, read from its FIB; null when it is not encrypted.</summary>
    private static EncryptionInfo? Encryption(byte[] bytes)
    {
        using var root = RootStorage.Open(Expandable(bytes));
        var fib = Read(root, "WordDocument", 64) ?? throw new InvalidDataException("A Word document without its WordDocument stream");
        var flags = BitConverter.ToUInt16(fib, 0x0A);
        if ((flags & 0x0100) == 0) return null;
        var table = (flags & 0x0200) != 0 ? "1Table" : "0Table";
        var lKey = BitConverter.ToInt32(fib, 0x0E);
        var header = Read(root, table, Math.Clamp(lKey, 0, 4096)) ?? [];
        return new EncryptionInfo(table, header, (flags & 0x8000) != 0);
    }

    /// <summary>The document with its streams decrypted and its FIB marked clear; null when the password does not open it.</summary>
    private static byte[]? Decrypt(byte[] bytes, EncryptionInfo info, string password, CancellationToken ct)
    {
        if (info.Obfuscated || info.Header.Length < 52) return null;
        var major = BitConverter.ToUInt16(info.Header, 0);
        var minor = BitConverter.ToUInt16(info.Header, 2);
        Func<int, byte[]>? keyFor = (major, minor) switch
        {
            (1, 1) => BinaryRc4Key(info.Header, password),
            ( >= 2 and <= 4, 2) => CryptoApiKey(info.Header, password),
            _ => null,
        };
        if (keyFor is null) return null;

        using var source = RootStorage.Open(Expandable(bytes));
        var output = new MemoryStream();
        using (var target = RootStorage.Create(output, OpenMcdf.Version.V3, StorageModeFlags.LeaveOpen))
        {
            Copy(source, target, name =>
            {
                ct.ThrowIfCancellationRequested();
                if (name is not ("WordDocument" or "Data") && name != info.Table) return null;
                var data = Read(source, name, int.MaxValue)!;
                var plain = Crypt(data, keyFor);
                if (name == "WordDocument")
                {
                    data.AsSpan(0, Math.Min(FibClearBytes, data.Length)).CopyTo(plain);   // the FIB's head was never encrypted
                    BitConverter.TryWriteBytes(plain.AsSpan(0x0A), (ushort)(BitConverter.ToUInt16(plain, 0x0A) & ~0x0100));
                }
                else if (name == info.Table)
                    data.AsSpan(0, Math.Min(info.Header.Length, data.Length)).CopyTo(plain);   // nor the encryption header
                return plain;
            });
        }
        return output.ToArray();
    }

    /// <summary>RC4 in 512-byte blocks, each with its own key, the keystream restarting at each block.</summary>
    private static byte[] Crypt(byte[] data, Func<int, byte[]> keyFor)
    {
        var output = new byte[data.Length];
        for (int at = 0, block = 0; at < data.Length; at += BlockSize, block++)
        {
            var length = Math.Min(BlockSize, data.Length - at);
            PdfDocument.Rc4(keyFor(block), data.AsSpan(at, length).ToArray()).CopyTo(output, at);
        }
        return output;
    }

    /// <summary>Word 97's RC4 (MS-OFFCRYPTO 2.3.6): MD5 of the password cut to 40 bits, mixed with the salt; null when the
    /// password does not open the document.</summary>
    private static Func<int, byte[]>? BinaryRc4Key(byte[] header, string password)
    {
        var salt = header.AsSpan(4, 16).ToArray();
        var h0 = MD5.HashData(Encoding.Unicode.GetBytes(password)).AsSpan(0, 5).ToArray();
        var buffer = new List<byte>(336);
        for (var i = 0; i < 16; i++) { buffer.AddRange(h0); buffer.AddRange(salt); }
        var h1 = MD5.HashData([.. buffer]).AsSpan(0, 5).ToArray();
        byte[] Key(int block) => MD5.HashData([.. h1, .. BitConverter.GetBytes(block)]);
        var check = PdfDocument.Rc4(Key(0), header.AsSpan(20, 32).ToArray());
        return MD5.HashData(check.AsSpan(0, 16)).AsSpan().SequenceEqual(check.AsSpan(16, 16)) ? Key : null;
    }

    /// <summary>The RC4 CryptoAPI of Word 2002-2003 (MS-OFFCRYPTO 2.3.5): SHA-1 of the salt and the password; null when the
    /// password does not open the document.</summary>
    private static Func<int, byte[]>? CryptoApiKey(byte[] header, string password)
    {
        var headerSize = BitConverter.ToInt32(header, 8);
        var keyBits = BitConverter.ToInt32(header, 12 + 16);
        if (keyBits == 0) keyBits = 40;
        var verifier = 12 + headerSize;
        if (verifier + 4 + 16 + 16 + 4 + 20 > header.Length) return null;
        var salt = header.AsSpan(verifier + 4, 16).ToArray();
        var h0 = SHA1.HashData([.. salt, .. Encoding.Unicode.GetBytes(password)]);
        byte[] Key(int block)
        {
            var h = SHA1.HashData([.. h0, .. BitConverter.GetBytes(block)]);
            var key = new byte[Math.Max(16, keyBits / 8)];
            h.AsSpan(0, Math.Min(h.Length, keyBits / 8)).CopyTo(key);   // a 40-bit key is padded with zeros to 128 bits
            return keyBits == 40 ? key[..16] : key[..(keyBits / 8)];
        }
        var check = PdfDocument.Rc4(Key(0), [.. header.AsSpan(verifier + 20, 16), .. header.AsSpan(verifier + 40, 20)]);
        return SHA1.HashData(check.AsSpan(0, 16)).AsSpan().SequenceEqual(check.AsSpan(16, 20)) ? Key : null;
    }

    /// <summary>Copies every stream and storage of <paramref name="from"/>; a stream <paramref name="replace"/> returns bytes
    /// for is written with them instead.</summary>
    private static void Copy(Storage from, Storage to, Func<string, byte[]?> replace)
    {
        foreach (var entry in from.EnumerateEntries())
        {
            if (entry.Type == EntryType.Storage)
                Copy(from.OpenStorage(entry.Name), to.CreateStorage(entry.Name), _ => null);
            else if (entry.Type == EntryType.Stream)
            {
                var bytes = replace(entry.Name) ?? Read(from, entry.Name, int.MaxValue)!;
                using var stream = to.CreateStream(entry.Name);
                stream.Write(bytes);
            }
        }
    }

    /// <summary>The bytes in a stream that can grow: both libraries set the length of a container whose size is not a whole
    /// number of sectors, which a stream over a fixed array refuses.</summary>
    internal static MemoryStream Expandable(byte[] bytes)
    {
        var stream = new MemoryStream(bytes.Length);
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }

    private static byte[]? Read(Storage storage, string name, int max)
    {
        try
        {
            using var stream = storage.OpenStream(name);
            if (stream.Length > DocumentText.MaxBytes) throw new DocumentLimitException("A Word stream past its size limit");
            var bytes = new byte[Math.Min(stream.Length, max)];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
    }
}
