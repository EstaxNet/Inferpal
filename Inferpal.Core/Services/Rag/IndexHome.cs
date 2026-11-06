using Inferpal.Services.Tools;
using Microsoft.Data.Sqlite;

namespace Inferpal.Services.Rag;

/// <summary>
/// Where what can be rebuilt lives: the code index, the documentation index, the debugger's test scaffold. On Windows,
/// outside the roaming profile, which would carry hundreds of megabytes at every sign-in; elsewhere nothing roams, and
/// nothing moves.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Nothing here removes a base that may still serve. Two editors update separately: a base removed under an older
/// version is built again by it, embeddings included, and the next sweep would remove it again. So a base is copied and
/// read from the new place, what an older version writes afterwards is copied again, and the old base goes only once
/// nothing has written it for <see cref="Grace"/> since its copy.
/// </para>
/// <para>
/// ⚠ Opening a base never copies it: the host opens the index on its way to answering <c>initialize</c>. A base not yet
/// copied is read where it is (<see cref="Resolve"/>), which is also all a failed copy needs.
/// </para>
/// </remarks>
internal static class IndexHome
{
    /// <summary>The table that says whether a code index holds anything.</summary>
    internal const string CodeChunks = "chunks";

    /// <summary>The table that says whether the documentation index holds anything.</summary>
    internal const string DocChunks = "doc_chunks";

    /// <summary>How long an old base stays unwritten after its copy before it goes.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromDays(14);

    /// <summary>The age under which a file may be something another process is still writing.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromDays(1);

    private const string CopySuffix = ".moving";

    /// <summary>Beside an old base, dated when it was copied: no file date says so on every system.</summary>
    private const string CopiedMark = ".copied";

    /// <summary>Beside a base, dated when it was last opened: its own date says when it was written, and decides what is
    /// copied again.</summary>
    private const string OpenedMark = ".opened";

    /// <summary>Beside a base never opened by this version, dated when the 90 days started: it says nothing about an opening.
    /// </summary>
    private const string SeenMark = ".seen";

    private static readonly string[] Logs = ["-wal", "-shm", "-journal"];
    private static readonly string[] Sides = [.. Logs, CopiedMark, OpenedMark, SeenMark];

    private static readonly object Gate = new();
    private static Task? _moved;

    /// <summary>The folder for <paramref name="name"/>, whether or not it exists yet.</summary>
    internal static string FolderOf(string name) => Path.Combine(
        UserFolders.Of(OperatingSystem.IsWindows() ? Environment.SpecialFolder.LocalApplicationData
                                                   : Environment.SpecialFolder.ApplicationData),
        "Inferpal", name);

    /// <summary>Where earlier versions kept <paramref name="name"/>; <c>null</c> when that is where it stays.</summary>
    internal static string? OldFolderOf(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var old = Path.Combine(UserFolders.Of(Environment.SpecialFolder.ApplicationData), "Inferpal", name);
        return SameFolder(old, FolderOf(name)) ? null : old;
    }

    /// <summary>Whether two folders are one, links followed.</summary>
    /// <remarks>⚠ Compared as spelled, a junction from one to the other makes every base "in both places", and the sweep
    /// removes the only copy.</remarks>
    private static bool SameFolder(string a, string b) =>
        PathComparer.SameDirectory(PathSanitizer.Canonical(a), PathSanitizer.Canonical(b));

    /// <summary>The file to open: <paramref name="file"/> when it exists or nothing older does, so that a new base is
    /// born in the right place and one not yet copied is read, never built again.</summary>
    internal static string Resolve(string file, string? oldFile) =>
        oldFile is null || File.Exists(file) || !File.Exists(oldFile) ? file : oldFile;

    /// <summary>When a base was last written, its write-ahead log included.</summary>
    /// <remarks>⚠ An empty log is no write: opening a base creates one, and it would date the index to that moment.</remarks>
    internal static DateTime WrittenAt(string file)
    {
        var written = File.GetLastWriteTimeUtc(file);
        var log     = new FileInfo(file + "-wal");
        return log.Exists && log.Length > 0 && log.LastWriteTimeUtc > written ? log.LastWriteTimeUtc : written;
    }

    /// <summary>Starts the sweep of this process, once.</summary>
    internal static void MoveInBackground()
    {
        lock (Gate) _moved ??= Task.Run(MoveEverything);
    }

    /// <summary>Completes once this process's sweep has ended, at once when it has none.</summary>
    /// <remarks>⚠ Awaited by what WRITES a base: a pass opened on the old file keeps writing there, and what it computes
    /// after the copy would be missing from the base the next opening reads. A reader needs no wait: the old base is
    /// there. Unbounded on purpose: the index reads the same disk, so a sweep that hangs leaves nothing to read anyway.
    /// </remarks>
    internal static Task MovedAsync(CancellationToken ct)
    {
        lock (Gate) return _moved?.WaitAsync(ct) ?? Task.CompletedTask;
    }

    private static void MoveEverything()
    {
        try
        {
            // ⚠ Before the first connection: Visual Studio's load context reads no deps.json.
            SqliteBootstrapper.EnsureInitialized();
            var now = DateTime.UtcNow;
            if (OldFolderOf("rag") is { } code) Sweep(FolderOf("rag"), code, CodeChunks, now);
            if (OldFolderOf("docs") is { } docs) Sweep(FolderOf("docs"), docs, DocChunks, now);
            // Built again at each use, so nothing to copy.
            if (OldFolderOf("testrepro") is { } scaffold && Directory.Exists(scaffold)) Directory.Delete(scaffold, recursive: true);
        }
        catch (Exception ex) { Diagnostics.Swallow("IndexHome.MoveEverything", ex); }
    }

    /// <summary>One pass over <paramref name="oldDir"/>: each base copied to <paramref name="dir"/>, the largest first, and
    /// what has served its time removed. A base that cannot be read or copied stays where it is; the next process tries
    /// again.</summary>
    internal static void Sweep(string dir, string oldDir, string chunkTable, DateTime now)
    {
        if (!Directory.Exists(oldDir) || SameFolder(dir, oldDir)) return;
        DropCopiesLeftBehind(dir);

        var bases = new DirectoryInfo(oldDir).EnumerateFiles()
            .Where(f => f.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Length)
            .Select(f => f.FullName)
            .ToList();
        foreach (var old in bases)
        {
            try { Settle(old, Path.Combine(dir, Path.GetFileName(old)), chunkTable, now); }
            catch (Exception ex) { Diagnostics.Swallow("IndexHome.Sweep", ex); }
        }
        try { DropLeftovers(oldDir, now); }
        catch (Exception ex) { Diagnostics.Swallow("IndexHome.DropLeftovers", ex); }
    }

    private static void Settle(string old, string file, string chunkTable, DateTime now)
    {
        // Before the first connection: closing it folds a leftover log into the base, and dates the base now.
        var written = WrittenAt(old);
        if (!HasRows(old, chunkTable))
        {
            if (now - written >= Settled) Remove(old);
            return;
        }

        var exists = File.Exists(file);
        // Later than the copy: an older version wrote since, and that work would go with the old base.
        if (!exists || written > WrittenAt(file)) Copy(old, file, written, replace: exists);
        // After the reads: the first connection may have folded a log, which dates the old base now.
        var mark = old + CopiedMark;
        var seen = WrittenAt(old);
        if (!File.Exists(mark) || seen > File.GetLastWriteTimeUtc(mark)) Stamp(mark, seen > now ? seen : now);

        var copied = File.GetLastWriteTimeUtc(mark);
        if (now - (written > copied ? written : copied) >= Grace && HasRows(file, chunkTable)) Remove(old);
    }

    /// <summary>The copy, under a name of its own until it is whole: an interrupted one leaves no base behind.</summary>
    /// <remarks>⚠ <c>VACUUM INTO</c>, never a file copy: it reads through the log and stays consistent while another
    /// process writes. And it writes a rollback-journal file: left so, the first opening rewrites the header, and the date
    /// the settings show with it.</remarks>
    internal static void Copy(string old, string file, DateTime written, bool replace)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var copy = $"{file}.{Guid.NewGuid():N}{CopySuffix}";
        try
        {
            using (var source = Open(old))
            using (var vacuum = source.CreateCommand())
            {
                vacuum.CommandText = "VACUUM INTO $copy";
                vacuum.Parameters.AddWithValue("$copy", copy);
                vacuum.ExecuteNonQuery();
            }
            using (var target = Open(copy))
            using (var journal = target.CreateCommand())
            {
                journal.CommandText = "PRAGMA journal_mode=WAL";
                journal.ExecuteNonQuery();
            }
            File.SetLastWriteTimeUtc(copy, written);
            Publish(copy, file, replace);
        }
        finally { File.Delete(copy); }
    }

    private static void Publish(string copy, string file, bool replace)
    {
        if (replace)
        {
            // ⚠ The logs first: one left beside another base would replay into it. An editor that has the base open holds
            // them, and this throws with the base as it was.
            foreach (var log in Logs) File.Delete(file + log);
            File.Move(copy, file, overwrite: true);
            return;
        }
        // Never over a base another process has just published: it may be writing to it.
        try { File.Move(copy, file, overwrite: false); }
        catch (IOException) when (File.Exists(file)) { }
    }

    private static bool HasRows(string file, string chunkTable)
    {
        try
        {
            using var conn = Open(file);
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM {chunkTable})";
            return cmd.ExecuteScalar() is long and not 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { return false; }   // no such table: a base never filled
    }

    private static void Stamp(string mark, DateTime at)
    {
        File.WriteAllBytes(mark, []);
        File.SetLastWriteTimeUtc(mark, at);
    }

    /// <summary>Whether an older version has written <paramref name="old"/> since the last sweep.</summary>
    internal static bool WrittenSinceCopy(string old)
    {
        var mark = old + CopiedMark;
        return File.Exists(mark) && WrittenAt(old) > File.GetLastWriteTimeUtc(mark);
    }

    /// <summary>When <paramref name="file"/> was last opened; <c>null</c> when nothing has noted it yet.</summary>
    internal static DateTime? OpenedAt(string file) =>
        File.Exists(file + OpenedMark) ? File.GetLastWriteTimeUtc(file + OpenedMark) : null;

    /// <summary>Notes that <paramref name="file"/> was opened, at most once an hour: every operation opens its base.</summary>
    internal static void NoteOpened(string file, DateTime now)
    {
        try
        {
            if (OpenedAt(file) is not { } opened || now - opened >= TimeSpan.FromHours(1)) Stamp(file + OpenedMark, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostics.Swallow("IndexHome.NoteOpened", ex); }
    }

    /// <summary>When the 90 days of a base never opened by this version started; <c>null</c> when they have not.</summary>
    internal static DateTime? SeenAt(string file) =>
        File.Exists(file + SeenMark) ? File.GetLastWriteTimeUtc(file + SeenMark) : null;

    /// <summary>Starts the 90 days of <paramref name="file"/>.</summary>
    internal static void NoteSeen(string file, DateTime now) => Stamp(file + SeenMark, now);

    /// <summary>Holds <paramref name="file"/> for as long as an index service uses it: under Windows the mark cannot be
    /// removed while it is open, from any process, and a crash lets it go.</summary>
    /// <remarks>⚠ The handle of a SQLite connection is no such hold: the pool closes an idle one after a few minutes, and a
    /// window left open would lose its base under it. A pass still writing would then fail on a base with no table.</remarks>
    internal static IDisposable? Hold(string file)
    {
        try { return new FileStream(file + OpenedMark, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("IndexHome.Hold", ex);
            return null;
        }
    }

    /// <summary>Removes the mark of <paramref name="file"/>; held by an index service, it throws and nothing has moved.</summary>
    internal static void DropOpened(string file) => File.Delete(file + OpenedMark);

    /// <summary>A connection of its own: a pooled one would stay open on a file this class is about to remove.</summary>
    internal static SqliteConnection Open(string file)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>A base and what lies beside it. Its mark, then the base: held, it throws and the base stays whole.</summary>
    internal static void Remove(string old)
    {
        DropOpened(old);
        File.Delete(old);
        foreach (var side in Sides) File.Delete(old + side);
    }

    /// <summary>Copies a killed process left. Held means another process is still writing one; there is no date to read,
    /// and a killed copy can be as large as the base.</summary>
    private static void DropCopiesLeftBehind(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var copy in Directory.EnumerateFiles(dir, $"*{CopySuffix}*"))
        {
            try { File.Delete(copy); }
            catch (IOException) { }
        }
    }

    private static void DropLeftovers(string oldDir, DateTime now)
    {
        foreach (var file in Directory.EnumerateFiles(oldDir))
            if (Sides.FirstOrDefault(s => file.EndsWith(s, StringComparison.OrdinalIgnoreCase)) is { } side
                && !File.Exists(file[..^side.Length]) && now - File.GetLastWriteTimeUtc(file) >= Settled)
                File.Delete(file);

        if (!Directory.EnumerateFileSystemEntries(oldDir).Any()) Directory.Delete(oldDir);
    }
}
