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

    private static readonly string[] Logs = ["-wal", "-shm", "-journal"];

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

        var mark = old + CopiedMark;
        var exists = File.Exists(file);
        // Later than the copy: an older version wrote since, and that work would go with the old base.
        var copy = !exists || written > WrittenAt(file);
        if (copy) Copy(old, file, written, replace: exists);
        if (copy || !File.Exists(mark))
        {
            File.WriteAllBytes(mark, []);
            File.SetLastWriteTimeUtc(mark, now);
        }

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

    /// <summary>A connection of its own: a pooled one would stay open on a file this class is about to remove.</summary>
    private static SqliteConnection Open(string file)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>A base and what lies beside it. The base first: held by an editor, it throws and nothing else is touched.
    /// </summary>
    private static void Remove(string old)
    {
        File.Delete(old);
        File.Delete(old + CopiedMark);
        foreach (var log in Logs) File.Delete(old + log);
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
            if (Logs.Append(CopiedMark).FirstOrDefault(s => file.EndsWith(s, StringComparison.OrdinalIgnoreCase)) is { } side
                && !File.Exists(file[..^side.Length]) && now - File.GetLastWriteTimeUtc(file) >= Settled)
                File.Delete(file);

        if (!Directory.EnumerateFileSystemEntries(oldDir).Any()) Directory.Delete(oldDir);
    }
}
