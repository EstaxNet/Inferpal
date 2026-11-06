using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The indexes leave the roaming profile without ever being built again: a base is copied, read from the new place, and
/// the old one goes only when nothing can still need it. Each test sweeps folders of its own: the sweep takes them as
/// arguments, so nothing here reaches the real profile.
/// </summary>
public sealed class IndexHomeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"home-{Guid.NewGuid():N}");
    private string Local => Path.Combine(_root, "local");
    private string Old => Path.Combine(_root, "roaming");
    private string OldBase => Path.Combine(Old, "abc.db");
    private string LocalBase => Path.Combine(Local, "abc.db");

    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    public IndexHomeTests() => Directory.CreateDirectory(Old);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static SqliteConnection Connect(string file)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    private static void Run(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>A base as the index leaves it: WAL, closed, with <paramref name="rows"/> chunks.</summary>
    private static void MakeBase(string file, int rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var conn = Connect(file);
        Run(conn, "PRAGMA journal_mode=WAL; CREATE TABLE chunks (id INTEGER PRIMARY KEY, content_hash TEXT NOT NULL)");
        for (var i = 0; i < rows; i++) Run(conn, $"INSERT INTO chunks (content_hash) VALUES ('h{i}')");
    }

    private static List<string> Hashes(string file)
    {
        using var conn = Connect(file);
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT content_hash FROM chunks ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var hashes = new List<string>();
        while (reader.Read()) hashes.Add(reader.GetString(0));
        return hashes;
    }

    private void Age(string file, DateTime written)
    {
        File.SetLastWriteTimeUtc(file, written);
        if (File.Exists(file + "-wal")) File.Delete(file + "-wal");
    }

    private void Sweep(DateTime now) => IndexHome.Sweep(Local, Old, IndexHome.CodeChunks, now);

    /// <summary>What an older version does after the copy: a chunk more, dated <paramref name="written"/>.</summary>
    private void WriteMore(string file, DateTime written)
    {
        using (var conn = Connect(file)) Run(conn, "INSERT INTO chunks (content_hash) VALUES ('written-after')");
        Age(file, written);
    }

    // ── Which file opens ─────────────────────────────────────────────

    [Fact]
    public void ABaseNotYetCopied_IsReadWhereItIs_NeverBuiltAgain()
    {
        MakeBase(OldBase, 2);
        Assert.Equal(OldBase, IndexHome.Resolve(LocalBase, OldBase));
    }

    [Fact]
    public void ACopiedBase_IsReadFromTheNewPlace_AndANewOneIsBornThere()
    {
        Assert.Equal(LocalBase, IndexHome.Resolve(LocalBase, OldBase));
        MakeBase(OldBase, 2);
        MakeBase(LocalBase, 2);
        Assert.Equal(LocalBase, IndexHome.Resolve(LocalBase, OldBase));
        Assert.Equal(LocalBase, IndexHome.Resolve(LocalBase, null));
    }

    [Fact]
    public void AnIndexPointedElsewhere_NeverLooksAtTheOldPlace()
    {
        // The suite, the probes and EmbedBench redirect the folder: the real base of the same root would answer for them.
        Assert.Null(RagDatabase.OldDir);
        Assert.Null(Inferpal.Services.Docs.DocsDatabase.OldDir);
        var file = RagDatabase.PathFor(Path.Combine(_root, "never-indexed"));
        Assert.StartsWith(TestRagStore.Dir, file, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(file));
    }

    // ── The copy ─────────────────────────────────────────────────────

    [Fact]
    public void ACopy_KeepsEveryChunk_AndLeavesTheOldBaseInPlace()
    {
        MakeBase(OldBase, 40);
        Sweep(Now);
        Assert.Equal(Hashes(OldBase), Hashes(LocalBase));
        Assert.Equal(40, Hashes(LocalBase).Count);
        Assert.True(File.Exists(OldBase));
        Assert.Empty(Directory.EnumerateFiles(Local, "*.moving*"));
    }

    [Fact]
    public void WhatIsStillInTheLog_ReachesTheCopy()
    {
        MakeBase(OldBase, 1);
        using var writer = Connect(OldBase);
        Run(writer, "PRAGMA wal_autocheckpoint=0");
        Run(writer, "INSERT INTO chunks (content_hash) VALUES ('only-in-the-log')");
        Assert.True(new FileInfo(OldBase + "-wal").Length > 0, "the row did not stay in the log");

        Sweep(Now);

        Assert.Contains("only-in-the-log", Hashes(LocalBase));
    }

    [Fact]
    public void ACopy_KeepsItsDate_AndOpensWithoutRewritingIt()
    {
        MakeBase(OldBase, 3);
        var written = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        Age(OldBase, written);

        Sweep(Now);

        Assert.Equal(written, File.GetLastWriteTimeUtc(LocalBase));
        using var conn = Connect(LocalBase);
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", cmd.ExecuteScalar());
    }

    [Fact]
    public void ALogLeftBehind_DatesTheCopy_NotTheSweepThatFoldedIt()
    {
        // The pool keeps connections open: an editor that exits leaves its log. The sweep's first connection folds it.
        var live = Path.Combine(_root, "live.db");
        MakeBase(live, 1);
        using (var writer = Connect(live))
        {
            Run(writer, "PRAGMA wal_autocheckpoint=0");
            Run(writer, "INSERT INTO chunks (content_hash) VALUES ('in-the-log')");
            File.Copy(live, OldBase);
            File.Copy(live + "-wal", OldBase + "-wal");
        }
        var built = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var logged = built.AddDays(2);
        File.SetLastWriteTimeUtc(OldBase, built);
        File.SetLastWriteTimeUtc(OldBase + "-wal", logged);

        Sweep(Now);

        Assert.Equal(logged, File.GetLastWriteTimeUtc(LocalBase));
        Assert.Contains("in-the-log", Hashes(LocalBase));
    }

    [Fact]
    public void AnEmptyLog_IsNoWrite_ButAFullOneIs()
    {
        var file = Path.Combine(Old, "dated.db");
        File.WriteAllText(file, "x");
        var written = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, written);

        File.WriteAllBytes(file + "-wal", []);
        Assert.Equal(written, IndexHome.WrittenAt(file));

        var later = written.AddDays(3);
        File.WriteAllText(file + "-wal", "x");
        File.SetLastWriteTimeUtc(file + "-wal", later);
        Assert.Equal(later, IndexHome.WrittenAt(file));
    }

    [Fact]
    public void TwoSweepsAtOnce_PublishOneBase_WithEveryChunk()
    {
        MakeBase(OldBase, 200);
        Parallel.For(0, 4, _ => Sweep(Now));
        Assert.Equal(200, Hashes(LocalBase).Count);
        Assert.Empty(Directory.EnumerateFiles(Local, "*.moving*"));
    }

    [Fact]
    public void ADamagedBase_StaysWhereItIs_AndNothingIsPublished()
    {
        File.WriteAllText(OldBase, "this is not a database, and it is longer than the header check would need it to be");
        File.SetLastWriteTimeUtc(OldBase, Now.AddDays(-400));
        Sweep(Now);
        Assert.True(File.Exists(OldBase));
        Assert.False(File.Exists(LocalBase));
    }

    [Fact]
    public void TheSameFolderTwice_MovesAndRemovesNothing()
    {
        // Without this, every base would be "in both places" and the sweep would remove the live one.
        MakeBase(OldBase, 3);
        var longAfter = DateTime.UtcNow.AddDays(100);
        IndexHome.Sweep(Old, Old, IndexHome.CodeChunks, longAfter);
        IndexHome.Sweep(Old + Path.DirectorySeparatorChar, Old, IndexHome.CodeChunks, longAfter);
        Assert.Equal(3, Hashes(OldBase).Count);
    }

    [Fact]
    public void AFolderLinkedToTheOldOne_IsTheSameFolder()
    {
        // Compared as spelled, every base is "in both places", and the grace removes the only copy.
        MakeBase(OldBase, 3);
        var link = Path.Combine(_root, "linked");
        if (OperatingSystem.IsWindows())
        {
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Old}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            mklink.WaitForExit();
        }
        else Directory.CreateSymbolicLink(link, Old);
        Assert.True(Directory.Exists(link), "the link was not made");

        var longAfter = DateTime.UtcNow.AddDays(100);
        IndexHome.Sweep(link, Old, IndexHome.CodeChunks, longAfter);
        IndexHome.Sweep(link, Old, IndexHome.CodeChunks, longAfter.AddDays(100));

        Assert.Equal(3, Hashes(OldBase).Count);
        Directory.Delete(link);
    }

    [Fact]
    public void ABasePublishedMeanwhile_IsNotOverwritten()
    {
        MakeBase(OldBase, 5);
        MakeBase(LocalBase, 1);
        IndexHome.Copy(OldBase, LocalBase, Now, replace: false);
        Assert.Single(Hashes(LocalBase));
        Assert.Empty(Directory.EnumerateFiles(Local, "*.moving*"));
    }

    [Fact]
    public void WhatAnOlderVersionWritesAfterTheCopy_ReachesTheNewPlace()
    {
        MakeBase(OldBase, 2);
        Age(OldBase, Now.AddDays(-30));
        Sweep(Now);
        Assert.Equal(2, Hashes(LocalBase).Count);

        WriteMore(OldBase, Now.AddDays(1));
        Sweep(Now.AddDays(2));

        Assert.Contains("written-after", Hashes(LocalBase));
        Assert.Equal(Now.AddDays(1), File.GetLastWriteTimeUtc(LocalBase));
    }

    [Fact]
    public void AReplacedBase_LosesTheLogOfTheOneBefore()
    {
        // A log left beside another base replays into it: frames of the local base over the copy of the old one.
        MakeBase(OldBase, 2);
        Age(OldBase, Now.AddDays(1));
        var live = Path.Combine(_root, "live.db");
        MakeBase(live, 1);
        using (var writer = Connect(live))
        {
            Run(writer, "PRAGMA wal_autocheckpoint=0");
            Run(writer, "INSERT INTO chunks (content_hash) VALUES ('stale-log')");
            Directory.CreateDirectory(Local);
            File.Copy(live, LocalBase);
            File.Copy(live + "-wal", LocalBase + "-wal");
        }
        File.SetLastWriteTimeUtc(LocalBase, Now.AddDays(-5));
        File.SetLastWriteTimeUtc(LocalBase + "-wal", Now.AddDays(-5));

        Sweep(Now.AddDays(2));

        Assert.Equal(["h0", "h1"], Hashes(LocalBase));
    }

    [Fact]
    public void ALocalBaseWrittenSince_IsNotReplaced()
    {
        MakeBase(OldBase, 2);
        Age(OldBase, Now.AddDays(-30));
        Sweep(Now);
        WriteMore(LocalBase, Now.AddDays(3));
        WriteMore(OldBase, Now.AddDays(1));

        Sweep(Now.AddDays(4));

        Assert.Equal(3, Hashes(LocalBase).Count);
        Assert.Equal(Now.AddDays(3), File.GetLastWriteTimeUtc(LocalBase));
    }

    [Fact]
    public void ALocalBaseAnEditorHoldsOpen_IsNotReplaced()
    {
        if (!OperatingSystem.IsWindows()) return;   // nothing moves elsewhere, and an open file can be replaced there
        MakeBase(OldBase, 2);
        Age(OldBase, Now.AddDays(-30));
        Sweep(Now);
        using var editor = Connect(LocalBase);
        Run(editor, "INSERT INTO chunks (content_hash) VALUES ('held')");
        var written = DateTime.UtcNow.AddDays(1);
        WriteMore(OldBase, written);

        Sweep(written.AddDays(1));

        Assert.Contains("held", Hashes(LocalBase));
        Assert.DoesNotContain("written-after", Hashes(LocalBase));
        Assert.Empty(Directory.EnumerateFiles(Local, "*.moving*"));
    }

    // ── What goes, and when ──────────────────────────────────────────

    [Fact]
    public void AnOldBaseStays_UntilItAndItsCopyHaveBeenLeftAloneLongEnough()
    {
        MakeBase(OldBase, 3);
        Age(OldBase, Now.AddDays(-100));
        Sweep(Now);
        Assert.True(File.Exists(OldBase), "removed on the sweep that copied it");

        Sweep(Now.AddDays(13));
        Assert.True(File.Exists(OldBase), "removed before the grace ended");

        Sweep(Now.AddDays(15));
        Assert.False(File.Exists(OldBase));
        Assert.False(File.Exists(OldBase + ".copied"));
        Assert.Equal(3, Hashes(LocalBase).Count);
    }

    [Fact]
    public void AnOldBaseAnOlderVersionStillWrites_IsNeverRemovedUnderIt()
    {
        MakeBase(OldBase, 3);
        Age(OldBase, Now.AddDays(-100));
        Sweep(Now);

        var writtenSince = Now.AddDays(20);
        Age(OldBase, writtenSince);
        Sweep(writtenSince.AddDays(13));
        Assert.True(File.Exists(OldBase), "removed under the version still writing it");

        // That write was copied, and the grace starts again from there.
        Assert.Equal(writtenSince, File.GetLastWriteTimeUtc(LocalBase));
        Sweep(writtenSince.AddDays(13 + 15));
        Assert.False(File.Exists(OldBase));
    }

    [Fact]
    public void AnOldBaseStays_WhenItsCopyHasNoChunk()
    {
        MakeBase(OldBase, 3);
        MakeBase(LocalBase, 0);
        Age(OldBase, DateTime.UtcNow.AddDays(-100));
        var first = DateTime.UtcNow.AddDays(1);
        Sweep(first);
        Sweep(first.AddDays(100));
        Assert.True(File.Exists(OldBase));
    }

    [Fact]
    public void ABaseStillHeld_StaysWithoutAnError()
    {
        if (!OperatingSystem.IsWindows()) return;   // removing an open file is allowed elsewhere, and nothing moves there
        MakeBase(OldBase, 3);
        Age(OldBase, Now.AddDays(-100));
        Sweep(Now);
        using var held = new FileStream(OldBase, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Sweep(Now.AddDays(100));
        Assert.True(File.Exists(OldBase));
        Assert.True(File.Exists(OldBase + ".copied"), "what lies beside a held base went without it");
    }

    [Fact]
    public void AnEmptyBase_GoesWithoutACopy_OnceItIsADayOld()
    {
        MakeBase(OldBase, 0);
        var fresh = Path.Combine(Old, "fresh.db");
        MakeBase(fresh, 0);
        Age(OldBase, Now.AddDays(-2));
        Age(fresh, Now.AddHours(-1));

        Sweep(Now);

        Assert.False(File.Exists(OldBase));
        Assert.False(File.Exists(LocalBase));
        Assert.True(File.Exists(fresh), "an older version may have just created it");
    }

    [Fact]
    public void WhatABaseLeftBehind_Goes_AndThenTheFolder()
    {
        var log = Path.Combine(Old, "gone.db-wal");
        File.WriteAllText(log, "x");
        File.SetLastWriteTimeUtc(log, Now.AddDays(-2));
        Directory.CreateDirectory(Local);
        var copy = Path.Combine(Local, "abc.db.1234.moving");
        File.WriteAllText(copy, "x");

        Sweep(Now);

        Assert.False(File.Exists(copy), "a copy a killed process left stays until its base is copied again");
        Assert.False(Directory.Exists(Old));
    }

    [Fact]
    public void ALogBesideALiveBase_OrFreshlyLeft_Stays()
    {
        MakeBase(OldBase, 1);
        File.WriteAllBytes(OldBase + "-shm", []);
        File.SetLastWriteTimeUtc(OldBase + "-shm", Now.AddDays(-5));
        var fresh = Path.Combine(Old, "fresh.db-wal");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(fresh, Now.AddHours(-1));

        Sweep(Now);

        Assert.True(File.Exists(fresh), "another process may be about to use it");
        Assert.True(Directory.Exists(Old));
    }

    [Fact]
    public void ACopyStillBeingWritten_IsLeftAlone()
    {
        if (!OperatingSystem.IsWindows()) return;   // an open file can be removed elsewhere, and nothing moves there
        Directory.CreateDirectory(Local);
        var copy = Path.Combine(Local, "abc.db.1234.moving");
        using var writer = new FileStream(copy, FileMode.Create, FileAccess.Write, FileShare.Read);
        Sweep(Now);
        Assert.True(File.Exists(copy));
    }

    [Theory]
    [InlineData("Rag", "RagDatabase.cs", IndexHome.CodeChunks)]
    [InlineData("Docs", "DocsDatabase.cs", IndexHome.DocChunks)]
    public void TheTableTheSweepAsks_IsTheOneTheBaseFills(string folder, string file, string table)
    {
        // A wrong name reads as "no such table", which is a base never filled: removed after a day, chunks and all.
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Core", "Services", folder, file));
        Assert.Matches(new Regex($@"CREATE TABLE [^\n]*\b{table} \("), code);
    }

    /// <summary>A C# class large enough to pass the chunkers' threshold.</summary>
    private static string SampleClass(string name) => string.Join('\n',
        $"public class {name}", "{", "    public int One()   => 1;", "    public int Two()   => 2;",
        "    public int Three() => 3;", "    public int Four()  => 4;", "    public int Five()  => 5;",
        "    public int Six()   => 6;", "}");

    private static async Task RunPassAsync(string root, FakeInferenceProvider provider)
    {
        using var svc = new ProjectIndexService(provider, new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "embed-a" },
                                                new LspSemanticProvider());
        svc.StartIndexing(root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!(svc.Status.StartsWith("RAG: ✅", StringComparison.Ordinal) && !svc.IsIndexing))
        {
            Assert.True(DateTime.UtcNow < deadline, $"the pass did not finish (status: {svc.Status})");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task AnIndexThatMoved_IsNotEmbeddedAgain()
    {
        TestRagStore.Redirect();
        var root = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "Alpha.cs"), SampleClass("AlphaProbe"));
        var embedded = 0;
        var provider = new FakeInferenceProvider { OnEmbedding = _ => { Interlocked.Increment(ref embedded); return [0.1f, 0.2f, 0.3f]; } };

        await RunPassAsync(root, provider);
        Assert.True(embedded > 0, "the first pass embedded nothing");   // witness

        // The base as an earlier version left it, in the old place, and nothing in the new one.
        SqliteConnection.ClearAllPools();
        var live = new RagDatabase(root).DbPath;
        SqliteConnection.ClearAllPools();
        var before = Hashes(live);
        var old = Path.Combine(Old, Path.GetFileName(live));
        File.Move(live, old);
        foreach (var log in new[] { "-wal", "-shm" }) File.Delete(live + log);

        IndexHome.Sweep(TestRagStore.Dir, Old, IndexHome.CodeChunks, Now);
        Assert.Equal(before, Hashes(live));

        embedded = 0;
        await RunPassAsync(root, provider);
        Assert.Equal(0, embedded);
    }

    // ── Where each front-end starts it ───────────────────────────────

    [Theory]
    [InlineData("Inferpal.Host", "Program.cs")]
    [InlineData("Inferpal", "InferpalExtension.cs")]
    public void EachProcessStartsTheSweep(string project, string file)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), project, file));
        Assert.Contains("IndexHome.MoveInBackground()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void NoHostSweepsPerSession()
    {
        // An ACP agent opens one host per session: the sweep is the process's, not the host's.
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Host", "HostServer.cs"));
        Assert.DoesNotContain("MoveInBackground", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Rag", "ProjectIndexService.cs", "Task RunIndexingAsync(", "new RagDatabase(")]
    [InlineData("Rag", "ProjectIndexService.cs", "Task ReIndexFilesCoreAsync(", "new RagDatabase(")]
    [InlineData("Docs", "DocsIndexService.cs", "Task AddOrReindexAsync(", "new DocsDatabase(")]
    [InlineData("Docs", "DocsIndexService.cs", "Task RemoveAsync(", "new DocsDatabase(")]
    public void WhatWritesABase_WaitsForTheSweepFirst(string folder, string file, string method, string opening)
    {
        // A pass opened on the old file keeps writing there, and what it computes after the copy is lost to the next opening.
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Core", "Services", folder, file));
        var start = code.IndexOf(method, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{method} is not in {file}");
        var wait = code.IndexOf("IndexHome.MovedAsync", start, StringComparison.Ordinal);
        var open = code.IndexOf(opening, start, StringComparison.Ordinal);
        Assert.True(wait >= 0 && wait < open, $"{method} of {file} opens its base before the sweep has ended");
    }
}
