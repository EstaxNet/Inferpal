using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

namespace Inferpal.Services.Documents;

/// <summary>An entry of an archive: its path inside it (with <c>/</c>), and its size once read out.</summary>
internal sealed record ArchiveEntry(string Path, long Size);

/// <summary>
/// The archives the agent reads into without extracting them: zip and the formats built on it (nupkg, vsix, jar), 7z, RAR,
/// tar with its gz, bz2 and xz forms, and a lone <c>.gz</c>, <c>.bz2</c> or <c>.xz</c> (one entry, the name without the
/// extension). An entry is named <c>logs.zip!/app/log.txt</c>.
/// </summary>
/// <remarks>
/// ⚠ Nothing is written to disk, and nothing is trusted from a header: an entry is read through a counted copy that stops
/// at <see cref="MaxEntryBytes"/> whatever size the archive declares (a few kilobytes can declare, and inflate to,
/// gigabytes), and a listing stops at <see cref="MaxEntries"/>. A nested archive is named, not opened.
/// </remarks>
internal static class Archives
{
    /// <summary>The largest archive file opened.</summary>
    internal const long MaxArchiveBytes = 200L * 1024 * 1024;

    /// <summary>The most bytes one entry gives out.</summary>
    internal const long MaxEntryBytes = 8L * 1024 * 1024;

    /// <summary>Why an entry past <see cref="MaxEntryBytes"/> is not read out; <paramref name="inner"/> names it, or "it".</summary>
    internal static string TooLarge(string? inner) =>
        (inner is null ? "it" : $"'{inner}'") + $" is larger than {MaxEntryBytes / (1024 * 1024)} MB once read out";

    /// <summary>Why an encrypted entry that a password opens still does not read.</summary>
    private static string NotReadOut(ZipCryptOutcome outcome, string? inner) => outcome switch
    {
        ZipCryptOutcome.TooLarge    => TooLarge(inner),
        ZipCryptOutcome.Unsupported => "its encryption or compression method is not one Inferpal reads",
        _                           => "it could not be decrypted (damaged)",
    };

    /// <summary>The most entries a listing reads.</summary>
    internal const int MaxEntries = 20_000;

    /// <summary>The most entries a zip may declare: its whole central directory is loaded before any entry is read.</summary>
    internal const long MaxDeclaredEntries = 100_000;

    /// <summary>The most bytes a tar (or what its gzip inflates to) is read through, headers and skipped entries included.</summary>
    internal static long MaxStreamBytes { get; set; } = 1L << 30;

    private static readonly string[] ZipExtensions = [".zip", ".nupkg", ".snupkg", ".vsix", ".jar", ".war", ".apk", ".whl"];
    private static readonly string[] TarExtensions = [".tar", ".tgz", ".tar.gz", ".tbz", ".tbz2", ".tar.bz2", ".txz", ".tar.xz"];
    private static readonly string[] PackedExtensions = [".7z", ".rar"];
    private static readonly string[] SingleExtensions = [".gz", ".bz2", ".xz"];

    private enum Kind { None, Zip, Tar, Packed, Single }

    /// <summary>The reader for <paramref name="path"/>, by its extension; a tar's compression wins over a lone one.</summary>
    private static Kind KindOf(string path)
    {
        var name = path.ToLowerInvariant();
        return ZipExtensions.Any(name.EndsWith) ? Kind.Zip
             : TarExtensions.Any(name.EndsWith) ? Kind.Tar
             : PackedExtensions.Any(name.EndsWith) ? Kind.Packed
             : SingleExtensions.Any(name.EndsWith) ? Kind.Single
             : Kind.None;
    }

    /// <summary>Whether <paramref name="path"/> names an archive this reader opens, by its extension.</summary>
    internal static bool Handles(string path) => KindOf(path) != Kind.None;

    /// <summary>Splits <c>archive!/inner</c> into the archive's path and the entry's path inside it (with <c>/</c>, no
    /// leading slash); false when <paramref name="path"/> names no entry of an archive.</summary>
    internal static bool TrySplit(string? path, out string archive, out string inner)
    {
        archive = inner = string.Empty;
        if (string.IsNullOrEmpty(path)) return false;
        for (var at = path.IndexOf('!'); at > 0; at = path.IndexOf('!', at + 1))
        {
            var head = path[..at];
            var rest = path[(at + 1)..];
            if (!Handles(head) || rest.Length > 0 && rest[0] is not ('/' or '\\')) continue;
            archive = head;
            inner = rest.Replace('\\', '/').Trim('/');
            return true;
        }
        return false;
    }

    /// <summary>The entries of <paramref name="archive"/> under <paramref name="prefix"/> (empty: all of them), sorted;
    /// or why it cannot be read. <paramref name="more"/>: the listing stopped at <see cref="MaxEntries"/>.</summary>
    internal static (List<ArchiveEntry>? Entries, string? Problem) List(string archive, string prefix, out bool more, CancellationToken ct) =>
        List(archive, prefix, out more, out _, ct);

    /// <summary><see cref="List(string, string, out bool, CancellationToken)"/>, with the entries left out for their name
    /// (<see cref="Safe"/>), to be said.</summary>
    internal static (List<ArchiveEntry>? Entries, string? Problem) List(string archive, string prefix, out bool more, out int oddNames,
                                                                        CancellationToken ct)
    {
        more = false;
        oddNames = 0;
        if (Opens(archive) is { } problem) return (null, problem);
        var found = new List<ArchiveEntry>();
        var under = prefix.Length == 0 ? string.Empty : prefix.TrimEnd('/') + "/";
        try
        {
            var walked = 0;
            foreach (var (name, size, _) in Walk(archive, ct, open: false))
            {
                // Entries WALKED, not kept: a prefix that matches nothing would otherwise read the whole archive.
                if (++walked > MaxEntries) { more = true; break; }
                if (name is null) { oddNames++; continue; }
                if (under.Length == 0 || name.StartsWith(under, StringComparison.Ordinal)) found.Add(new ArchiveEntry(name, size));
            }
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            return (null, NotAnArchive(ex.Message));
        }
        found.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (found, null);
    }

    /// <summary>
    /// The passwords the person typed, by archive, for this process only. ⚠ Never written anywhere — not the settings, the
    /// conversation, a log — and never sent: only the entries they open leave this class.
    /// </summary>
    /// <remarks>A set per archive: one archive may hold entries under different passwords, and two reads of it may run in
    /// parallel.</remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, byte>> Passwords =
        new(PathComparer.Default);

    /// <summary>
    /// Opens <paramref name="entry"/> of the protected zip at <paramref name="archive"/> with the password the person typed:
    /// true when it does, and the password is kept in memory for that archive until the process ends.
    /// </summary>
    /// <returns><c>true</c>: opened; <c>false</c>: a wrong password; <c>null</c>: the password is not the problem — the
    /// entry does not read for another reason, kept in <see cref="UnreadableAfterPassword"/> for the reader to say.</returns>
    internal static bool? TryPassword(string archive, string entry, string password, CancellationToken ct = default)
    {
        if (password.Length == 0) return false;
        if (Opens(archive) is { } problem) { UnreadableAfterPassword[(archive, entry)] = problem; return null; }
        if (KindOf(archive) == Kind.Packed)
        {
            var packed = ReadPacked(archive, entry, [password], ct);
            if (packed.Locked || packed.OpenedWith is null) return false;
            // Kept only once an entry read through its checks: names in clear open with ANY password.
            if (packed.Bytes is not null) { Passwords.GetOrAdd(archive, _ => new(StringComparer.Ordinal))[password] = 0; return true; }
            UnreadableAfterPassword[(archive, entry)] = packed.Problem ?? "it could not be read";
            return null;
        }
        var outcome = ZipCrypt.Read(archive, entry, password, ct).Outcome;
        if (outcome == ZipCryptOutcome.WrongPassword) return false;
        if (outcome != ZipCryptOutcome.Opened)
        {
            UnreadableAfterPassword[(archive, entry)] = NotReadOut(outcome, null);
            return null;
        }
        Passwords.GetOrAdd(archive, _ => new(StringComparer.Ordinal))[password] = 0;
        return true;
    }

    /// <summary>Why an entry the person gave the right password for still does not read, until it is said.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Archive, string Entry), string> UnreadableAfterPassword = new();

    /// <summary>The bytes of <paramref name="inner"/> in <paramref name="archive"/>, or why not; <c>Locked</c>: the entry is
    /// encrypted and no password typed in this process opens it.</summary>
    internal static (byte[]? Bytes, string? Problem, bool Locked) Read(string archive, string inner, CancellationToken ct)
    {
        if (Opens(archive) is { } problem) return (null, problem, false);
        if (KindOf(archive) == Kind.Packed)
        {
            var packed = ReadPacked(archive, inner, [null, .. KeptPasswords(archive)], ct);
            return (packed.Bytes, packed.Problem, packed.Locked);
        }
        if (ZipExtensions.Any(archive.ToLowerInvariant().EndsWith) && IsEncryptedEntry(archive, inner))
        {
            // What no password can fix is said first: never a card for an entry that would not open anyway.
            if (ZipCrypt.CannotOpen(archive, inner) is { } why) return (null, why, false);
            var last = ZipCryptOutcome.WrongPassword;
            foreach (var password in Passwords.TryGetValue(archive, out var kept) ? kept.Keys : [])
            {
                var (opened, outcome) = ZipCrypt.Read(archive, inner, password, ct);
                if (outcome == ZipCryptOutcome.Opened) return (opened, null, false);
                if (outcome != ZipCryptOutcome.WrongPassword) last = outcome;
            }
            return last == ZipCryptOutcome.WrongPassword
                ? (null, PasswordProtected, true)
                : (null, NotReadOut(last, inner), false);
        }
        var plain = ReadPlain(archive, inner, ct);
        return (plain.Bytes, plain.Problem, false);
    }

    /// <summary>Whether .NET's reader marks <paramref name="inner"/> encrypted — the one judge, so a name the decryption's
    /// own directory reading would miss still never reaches <see cref="ReadPlain"/>.</summary>
    private static bool IsEncryptedEntry(string archive, string inner)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            return zip.Entries.Any(e => e.IsEncrypted && Safe(e.FullName) == inner);
        }
        catch (Exception ex) when (Unreadable(ex)) { return false; }
    }

    private static (byte[]? Bytes, string? Problem) ReadPlain(string archive, string inner, CancellationToken ct)
    {
        try
        {
            foreach (var (name, _, open) in Walk(archive, ct, open: true))
            {
                if (name is null || !string.Equals(name, inner, StringComparison.Ordinal)) continue;
                using var stream = open!();
                return ReadOut(stream, inner, ct);
            }
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            return (null, NotAnArchive(ex.Message));
        }
        return (null, NoEntry(inner));
    }

    /// <summary>An entry's bytes, counted as they come out of the decoder: never more than <see cref="MaxEntryBytes"/>,
    /// whatever the archive declares.</summary>
    private static (byte[]? Bytes, string? Problem) ReadOut(Stream stream, string inner, CancellationToken ct)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (copy.Length + read > MaxEntryBytes)
                return (null, TooLarge(inner));
            copy.Write(buffer, 0, read);
        }
        return (copy.ToArray(), null);
    }

    private static IEnumerable<string> KeptPasswords(string archive) =>
        Passwords.TryGetValue(archive, out var kept) ? kept.Keys : [];

    /// <summary>A 7z or a RAR, opened with the first of <paramref name="passwords"/> that opens it (<c>null</c>: none),
    /// its file entries read; <c>null</c> when none does, with what the last attempt threw.</summary>
    /// <remarks>
    /// ⚠ Opened by the reader its EXTENSION names, never by the signature: a zip renamed <c>.7z</c> would otherwise skip the
    /// zip's own guards (<see cref="DeclaredZipEntries"/>), and an ARJ or ACE renamed <c>.rar</c> reach decoders this class
    /// never meant to run. A wrong password on encrypted names throws as damage does: the two are told apart only by which
    /// password was tried, never by the message.
    /// </remarks>
    private static (SharpCompress.Archives.IArchive? Archive, List<SharpCompress.Archives.IArchiveEntry> Entries, string? OpenedWith, Exception? Failure)
        OpenPacked(string archive, IEnumerable<string?> passwords)
    {
        Exception? failure = null;
        var sevenZip = archive.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
        foreach (var password in passwords)
        {
            // The stream is ours: an open that throws halfway would otherwise leave the file held.
            Stream? file = null;
            SharpCompress.Archives.IArchive? opened = null;
            try
            {
                file = File.OpenRead(archive);
                var options = new SharpCompress.Readers.ReaderOptions { Password = password, LeaveStreamOpen = false };
                opened = sevenZip ? SharpCompress.Archives.SevenZip.SevenZipArchive.Open(file, options)
                                  : SharpCompress.Archives.Rar.RarArchive.Open(file, options);
                var entries = opened.Entries.Take((int)MaxDeclaredEntries).Where(e => !e.IsDirectory).ToList();
                return (opened, entries, password ?? string.Empty, null);
            }
            catch (Exception ex)
            {
                opened?.Dispose();
                file?.Dispose();
                if (!Unreadable(ex)) throw;
                failure = ex;
            }
        }
        return (null, [], null, failure);
    }

    /// <summary>
    /// <paramref name="inner"/> of a 7z or a RAR, tried with each of <paramref name="passwords"/> (<c>null</c>: none).
    /// <c>Locked</c>: it is protected and none of them opened it; <c>OpenedWith</c>: the password the archive opened with
    /// (empty: none was needed), even when the entry then does not read.
    /// </summary>
    private static (byte[]? Bytes, string? Problem, bool Locked, string? OpenedWith) ReadPacked(
        string archive, string inner, IReadOnlyList<string?> passwords, CancellationToken ct)
    {
        string? damage = null;
        var locked = false;
        var sevenZip = archive.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
        foreach (var password in passwords)
        {
            var (opened, entries, openedWith, failure) = OpenPacked(archive, [password]);
            if (opened is null)
            {
                // Without a password, a failure on encrypted names asks for one; with one, it was the wrong one.
                if (password is not null || AsksForPassword(failure))
                    locked = true;
                else damage = NotAnArchive(failure?.Message);
                continue;
            }
            using (opened)
            {
                var entry = entries.FirstOrDefault(e => e.Key is { } k && Safe(k) == inner);
                if (entry is null)
                    return (null, NoEntry(inner), false, openedWith);
                if (entry.IsEncrypted && password is null) { locked = true; continue; }
                if (entry.IsSplitAfter || entry.VolumeIndexFirst != entry.VolumeIndexLast)
                    return (null, "it continues in another volume of a split archive — open the first volume with 7-Zip", false, openedWith);
                try
                {
                    if (opened.IsSolid)
                    {
                        // ⚠ Solid: reaching an entry decodes every one before it. Their DECLARED sizes bound that work, since
                        // the decoder stops at them; past the budget the reading is refused, never started.
                        long before = 0;
                        foreach (var e in entries)
                        {
                            if (ReferenceEquals(e, entry)) break;
                            before += Math.Max(0, e.Size);
                        }
                        if (before > MaxStreamBytes)
                            return (null, $"it is a solid archive where reaching '{inner}' means decoding {before / (1024 * 1024)} MB first — extract it with 7-Zip", false, openedWith);
                    }
                    (byte[]? Bytes, string? Problem) plain;
                    if (sevenZip || !opened.IsSolid)
                    {
                        // A 7z entry's own stream decodes its block once, up to it; its reader would decode the block again
                        // for every entry it skips.
                        using var stream = entry.OpenEntryStream();
                        plain = ReadOut(stream, inner, ct);
                    }
                    else
                    {
                        // A solid RAR opens its entries only in order.
                        plain = (null, NoEntry(inner));
                        using var reader = opened.ExtractAllEntries();
                        while (reader.MoveToNextEntry())
                        {
                            ct.ThrowIfCancellationRequested();
                            if (reader.Entry.IsDirectory || reader.Entry.Key is not { } key || Safe(key) != inner) continue;
                            using var stream = reader.OpenEntryStream();
                            plain = ReadOut(stream, inner, ct);
                            break;
                        }
                    }
                    // ⚠ A 7z checks no CRC on this path, and its AES has no password check: a wrong key decodes to noise.
                    // The CRC is checked here (a RAR checks its own, keyed when encrypted).
                    if (sevenZip && plain.Bytes is { } read && entry.Crc != 0 && ZipCrypt.Crc32(read) != (uint)entry.Crc)
                        throw new InvalidDataException("its content does not match its checksum");
                    return (plain.Bytes, plain.Problem, false, openedWith);
                }
                catch (Exception ex) when (Unreadable(ex))
                {
                    // An encrypted entry that does not decode with a password typed: the password is wrong. Otherwise the
                    // archive is damaged, password or not.
                    if (password is not null && entry.IsEncrypted) { locked = true; continue; }
                    damage = NotAnArchive(ex.Message);
                }
            }
        }
        return locked ? (null, PasswordProtected, true, null) : (null, damage ?? "it could not be read", false, null);
    }

    /// <summary>What a damaged or hostile archive throws while it is read: said as unreadable, never a crash.</summary>
    /// <remarks>The decoders of a hostile archive also throw a bare <see cref="Exception"/>, an unimplemented branch, or
    /// fail to allocate the dictionary its header declares.</remarks>
    private static bool Unreadable(Exception ex) =>
        ex is InvalidDataException or IOException or FormatException or NotSupportedException or UnauthorizedAccessException
            or ArgumentException or OverflowException or InvalidOperationException or IndexOutOfRangeException
            or NotImplementedException or OutOfMemoryException or NullReferenceException
            or SharpCompress.Common.SharpCompressException
        || ex.GetType() == typeof(Exception);

    private static string? Opens(string archive)
    {
        if (!File.Exists(archive)) return "it does not exist";
        if (new FileInfo(archive).Length > MaxArchiveBytes) return $"it is larger than {MaxArchiveBytes / (1024 * 1024)} MB";
        if (ZipExtensions.Any(archive.ToLowerInvariant().EndsWith) && DeclaredZipEntries(archive) is > MaxDeclaredEntries and var n)
            return $"it declares {n} entries, more than {MaxDeclaredEntries}";
        return null;
    }

    /// <summary>What an archive that asks for a password is said to be.</summary>
    private const string PasswordProtected = "it is protected by a password";

    /// <summary>What an archive the reader refused is said to be, with the reader's reason.</summary>
    private static string NotAnArchive(string? why) => $"it could not be read as an archive ({why})";

    /// <summary>What an archive without <paramref name="inner"/> is said to hold, with the way to see what it holds.</summary>
    private static string NoEntry(string inner) => $"it holds no entry '{inner}' — list its entries with list_files";

    /// <summary>Whether the reader's failure says the archive asks for a password (encrypted entry names).</summary>
    private static bool AsksForPassword(Exception? failure) =>
        failure is SharpCompress.Common.CryptographicException || failure?.Message.Contains("password", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>How far from a zip's end its end record can start: the 22-byte record and a comment of up to 65,535 bytes.</summary>
    internal const int EndRecordReach = 66_000;

    /// <summary>The entry count a zip's end record declares (the Zip64 one when a classic field is saturated); 0 when it
    /// cannot be read — the open then says what is wrong.</summary>
    /// <remarks>⚠ Read before opening: a few megabytes can declare millions of directory records, all of them loaded, with
    /// no way to stop, by the archive reader.</remarks>
    internal static long DeclaredZipEntries(string archive)
    {
        try
        {
            using var f = File.OpenRead(archive);
            var tail = (int)Math.Min(f.Length, EndRecordReach);
            var buf = new byte[tail];
            f.Seek(-tail, SeekOrigin.End);
            f.ReadExactly(buf);
            for (var i = tail - 22; i >= 0; i--)
            {
                if (buf[i] != 0x50 || buf[i + 1] != 0x4b || buf[i + 2] != 5 || buf[i + 3] != 6) continue;
                long count = BitConverter.ToUInt16(buf, i + 10);
                var saturated = count == 0xFFFF || BitConverter.ToUInt32(buf, i + 12) == 0xFFFFFFFF
                                || BitConverter.ToUInt32(buf, i + 16) == 0xFFFFFFFF;
                if (!saturated || i < 20) return count;
                var locator = i - 20;
                if (buf[locator] != 0x50 || buf[locator + 1] != 0x4b || buf[locator + 2] != 6 || buf[locator + 3] != 7) return count;
                var zip64 = (long)BitConverter.ToUInt64(buf, locator + 8);
                if (zip64 < 0 || zip64 + 40 > f.Length) return long.MaxValue;
                var rec = new byte[40];
                f.Seek(zip64, SeekOrigin.Begin);
                f.ReadExactly(rec);
                return (long)Math.Min(BitConverter.ToUInt64(rec, 32), long.MaxValue);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return 0;
    }

    /// <summary>Every file entry, in the archive's order: its path (with <c>/</c>), its declared size, and how to open it
    /// (<c>null</c> when <paramref name="open"/> is false).</summary>
    /// <remarks>An entry whose name is left out (<see cref="Safe"/>) comes with a <c>null</c> name, so a listing can count it.</remarks>
    private static IEnumerable<(string? Name, long Size, Func<Stream>? Open)> Walk(string archive, CancellationToken ct, bool open)
    {
        var lower = archive.ToLowerInvariant();
        if (ZipExtensions.Any(lower.EndsWith))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;   // a folder
                if (Safe(entry.FullName) is not { } safeName) { yield return (null, 0, null); continue; }
                var e = entry;
                Func<Stream>? opener = open ? () => e.IsEncrypted ? throw new InvalidDataException("the entry is encrypted") : e.Open() : null;
                yield return (safeName, entry.Length, opener);
            }
            yield break;
        }
        var kind = KindOf(archive);
        if (kind == Kind.Packed)
        {
            var (opened, entries, _, failure) = OpenPacked(archive, [null, .. KeptPasswords(archive)]);
            if (opened is null)
                throw AsksForPassword(failure)
                    ? new InvalidDataException("even its entry names are protected by a password: reading one of its entries asks for it")
                    : failure ?? new InvalidDataException("it does not open");
            using (opened)
            {
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.Key is null || Safe(entry.Key) is not { } safeName) { yield return (null, 0, null); continue; }
                    yield return (safeName, entry.Size, null);   // read by ReadPacked, never through a walk
                }
            }
            yield break;
        }
        using var file = File.OpenRead(archive);
        if (kind == Kind.Single)
        {
            // One compressed file: one entry, its name without the extension; its size is known once read out.
            var inner = Path.GetFileNameWithoutExtension(archive);
            yield return (inner, -1, open ? () => Decompressing(file, lower) : null);
            yield break;
        }
        using Stream inflated = lower.EndsWith(".tar") ? file : Decompressing(file, lower);
        // Headers (a PAX header may declare gigabytes) and skipped entries are read through this one budget, Stop seen
        // on every read: the archive reader trusts the sizes it is given.
        using var source = new Bounded(inflated, MaxStreamBytes, ct);
        using var tar = new TarReader(source);
        while (tar.GetNextEntry(copyData: false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            var data = entry.DataStream;
            // A tar entry is readable only until the next one: opened in place, never kept.
            if (Safe(entry.Name) is not { } safeName) { yield return (null, 0, null); continue; }
            yield return (safeName, entry.Length, open && data is not null ? () => new NonClosing(data) : null);
        }
    }

    /// <summary>The decompressing stream for a <c>.gz</c>, <c>.bz2</c> or <c>.xz</c> (a tar's too), by the name's end.</summary>
    private static Stream Decompressing(Stream file, string lowerName) =>
        lowerName.EndsWith(".bz2") || lowerName.EndsWith(".tbz") || lowerName.EndsWith(".tbz2")
            ? new SharpCompress.Compressors.BZip2.BZip2Stream(file, SharpCompress.Compressors.CompressionMode.Decompress, true)
        : lowerName.EndsWith(".xz") || lowerName.EndsWith(".txz")
            ? new SharpCompress.Compressors.Xz.XZStream(file)
            : new GZipStream(file, CompressionMode.Decompress);

    /// <summary>An entry's path with <c>/</c>, without a leading <c>./</c> or <c>/</c> — never a leading dot of the name
    /// itself (<c>.env</c> stays <c>.env</c>).</summary>
    internal static string Normalize(string name)
    {
        var n = name.Replace('\\', '/');
        while (n.StartsWith("./", StringComparison.Ordinal)) n = n[2..];
        return n.TrimStart('/');
    }

    /// <summary>Whether the ignore files exclude <paramref name="entry"/> of <paramref name="archive"/>, judged as if the
    /// archive were a folder — by the listing reader (names the archive produced); a name that cannot be judged counts as
    /// excluded.</summary>
    internal static bool ExcludedEntry(string archive, string entry) => EntryExclusion(archive, entry).Excluded;

    /// <summary><see cref="ExcludedEntry"/>, with the rule that excludes it (<c>null</c> when the name itself could not be
    /// judged), for a reader that says why.</summary>
    internal static (bool Excluded, Governance.ContentExclusions.Exclusion? Why) EntryExclusion(string archive, string entry)
    {
        try
        {
            var judged = Path.Join(archive, entry.Replace('/', Path.DirectorySeparatorChar));
            if (!Path.GetFullPath(judged).StartsWith(Path.GetFullPath(archive) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return (true, null);
            var why = Governance.ContentExclusions.OfInWalk(judged);
            return (why is not null, why);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return (true, null); }
    }

    /// <summary>An entry's name as it is shown and judged, or <c>null</c> for one that is left out: rooted, a drive (<c>:</c>),
    /// a control character, or a <c>..</c> segment — names no honest archive holds, which would be judged outside it.</summary>
    internal static string? Safe(string name)
    {
        if (name.Length == 0 || name.Any(c => char.IsControl(c) || c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')
            || name.Contains(':') || name.StartsWith('/') || name.StartsWith('\\'))
            return null;
        var n = Normalize(name);
        return n.Length == 0 || n.Split('/').Any(s => s == "..") ? null : n;
    }

    /// <summary>A stream read through a budget, Stop seen on every read.</summary>
    private sealed class Bounded(Stream inner, long budget, CancellationToken ct) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            var n = inner.Read(buffer, offset, count);
            if ((_read += n) > budget) throw new InvalidDataException($"it expands past {budget / (1024 * 1024)} MB");
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>A tar entry's stream, which the reader owns: the caller's <c>using</c> must not close it.</summary>
    private sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
