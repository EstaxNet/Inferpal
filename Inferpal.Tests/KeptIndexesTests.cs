using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Lsp;
using Inferpal.Services.Presentation;
using Inferpal.Services.Rag;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What Inferpal keeps on this machine: the size of each repository's index, a way to forget it, and the old ones removed
/// and said. Folders of its own: <see cref="KeptIndexes"/> takes them as arguments, so nothing here reaches the real profile.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class KeptIndexesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"kept-{Guid.NewGuid():N}");
    private string Local => Path.Combine(_root, "local");
    private string Old => Path.Combine(_root, "roaming");

    private const string Shop = "aaaaaaaaaaaa.db";
    private const string Tablib = "bbbbbbbbbbbb.db";
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    public KeptIndexesTests() => Directory.CreateDirectory(Local);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A base as the index leaves it, for a repository at <paramref name="root"/>.</summary>
    private string MakeBase(string dir, string id, string root, int rows = 1, DateTime? opened = null)
    {
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, id);
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE chunks (id INTEGER PRIMARY KEY, file_path TEXT, rel_path TEXT)";
            cmd.ExecuteNonQuery();
            for (var i = 0; i < rows; i++)
            {
                cmd.CommandText = "INSERT INTO chunks (file_path, rel_path) VALUES ($f, $r)";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$f", Path.Combine(root, "src", $"F{i}.cs"));
                cmd.Parameters.AddWithValue("$r", $"src/F{i}.cs");
                cmd.ExecuteNonQuery();
            }
        }
        File.SetLastWriteTimeUtc(file, Now.AddDays(-200));
        if (opened is { } at) IndexHome.NoteOpened(file, at);
        return file;
    }

    private KeptIndexesModel Read(string? openId = null, string? oldDir = null, long docs = 0) =>
        KeptIndexes.Read(Local, oldDir, openId, Now, docs);

    // ── The list ─────────────────────────────────────────────────────

    [Fact]
    public void EachRepository_IsNamedByItsFolder_TheLargestFirst()
    {
        var small = MakeBase(Local, Tablib, Path.Combine(_root, "repos", "tablib"), rows: 1, opened: Now.AddDays(-12));
        MakeBase(Local, Shop, Path.Combine(_root, "repos", "shop-api"), rows: 400, opened: Now.AddDays(-1));
        Directory.CreateDirectory(Path.Combine(_root, "repos", "tablib"));
        Directory.CreateDirectory(Path.Combine(_root, "repos", "shop-api"));

        var model = Read(openId: Shop);

        Assert.Equal(["shop-api", "tablib"], model.Rows.Select(r => r.Name));
        Assert.True(model.Rows[0].OpenHere);
        Assert.False(model.Rows[1].OpenHere);
        Assert.Equal(Path.Combine(_root, "repos", "tablib"), model.Rows[1].Folder);
        Assert.EndsWith(Strings.KeptOpenedDaysAgo(12), model.Rows[1].Detail, StringComparison.Ordinal);
        Assert.EndsWith(Strings.KeptOpenNow, model.Rows[0].Detail, StringComparison.Ordinal);
        Assert.StartsWith(Strings.KeptSummary(2, "").TrimEnd(), model.Summary, StringComparison.Ordinal);
        Assert.True(File.Exists(small));
    }

    [Fact]
    public void AFolderGone_IsSaid_AndTheIndexStaysListed()
    {
        MakeBase(Local, Tablib, Path.Combine(_root, "gone", "tablib"), opened: Now.AddDays(-3));
        var row = Assert.Single(Read().Rows);
        Assert.Contains(Strings.KeptFolderNotFound, row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ABaseWithoutAChunk_ShowsNothing_AndADamagedOneIsStillListed()
    {
        MakeBase(Local, Shop, Path.Combine(_root, "shop"), rows: 0);
        File.WriteAllText(Path.Combine(Local, Tablib), "this is not a database, and it is long enough not to be taken for an empty one");
        var row = Assert.Single(Read().Rows);
        Assert.Equal(Tablib, row.Id);
        Assert.Contains(Strings.KeptUnknownFolder, row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ABaseNotYetCopied_IsListed_Once()
    {
        MakeBase(Old, Shop, Path.Combine(_root, "shop"));
        MakeBase(Old, Tablib, Path.Combine(_root, "tablib"));
        MakeBase(Local, Tablib, Path.Combine(_root, "tablib"));
        Assert.Equal(2, Read(oldDir: Old).Rows.Count);
    }

    [Fact]
    public void TheDocumentationIndex_IsSaidOnlyWhenThereIsOne()
    {
        Assert.Equal(Strings.KeptNote, Read().Note);
        Assert.Contains(Strings.KeptNoteDocs("2.7 MB").Split(',')[0], Read(docs: 2_700_000).Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(9_600_000, "9.6 MB")]
    [InlineData(523_000_000, "523 MB")]
    [InlineData(1_240_000_000, "1.2 GB")]
    public void ASize_ReadsAsThePageShowsIt(long bytes, string text)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal(text, KeptIndexes.Size(bytes));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void AnOlderVersionStillWriting_IsSaid_AndACopyLeftAloneIsNot()
    {
        var old = MakeBase(Old, Shop, Path.Combine(_root, "shop"));
        IndexHome.Sweep(Local, Old, IndexHome.CodeChunks, Now.AddDays(-5));
        Assert.Empty(Read(oldDir: Old).OlderWriter);

        File.SetLastWriteTimeUtc(old, Now.AddDays(-1));
        Assert.StartsWith(Strings.KeptOlderWriter(1, "").Split('(')[0], Read(oldDir: Old).OlderWriter, StringComparison.Ordinal);
    }

    // ── Forget ───────────────────────────────────────────────────────

    [Fact]
    public void Forget_RemovesTheBase_InBothPlaces_AndSaysWhatCameBack()
    {
        // Left in the old place, the next opening would read it and the index forgotten would come back.
        var old = MakeBase(Old, Tablib, Path.Combine(_root, "tablib"));
        var file = MakeBase(Local, Tablib, Path.Combine(_root, "tablib"));
        File.WriteAllBytes(file + "-shm", []);

        var (message, held) = KeptIndexes.Forget(Tablib, Local, Old, Shop);

        Assert.Null(held);
        Assert.StartsWith(Strings.KeptForgotten("tablib", "").Split(':')[0], message, StringComparison.Ordinal);
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(file + "-shm"));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void TheFolderOpenHere_IsNotForgotten_AndItsRowOffersNothing()
    {
        var file = MakeBase(Local, Shop, Path.Combine(_root, "shop-api"));
        var (message, held) = KeptIndexes.Forget(Shop, Local, null, Shop);
        Assert.Equal(Strings.KeptNotHere("shop-api"), message);
        Assert.Null(held);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void ABaseHeldOpen_IsNotForgotten_AndSaysWhy()
    {
        if (!OperatingSystem.IsWindows()) return;   // an open file can be removed elsewhere, and the index is built again
        var file = MakeBase(Local, Tablib, Path.Combine(_root, "tablib"));
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var (message, heldId) = KeptIndexes.Forget(Tablib, Local, null, null);

        Assert.Equal(Strings.KeptHeld, message);
        Assert.Equal(Tablib, heldId);
        Assert.True(File.Exists(file));
        Assert.Equal(Strings.KeptHeld, KeptIndexes.Read(Local, null, null, Now, 0, heldId: heldId).Rows[0].Note);
    }

    [Theory]
    [InlineData("..\\outside.db")]
    [InlineData("../outside.db")]
    public void AnIdFromAPage_NamesOnlyABase(string id)
    {
        // The id comes from a settings page: it is looked for among the bases, never used as a path.
        var outside = MakeBase(_root, "outside.db", Path.Combine(_root, "outside"));
        MakeBase(Local, Tablib, Path.Combine(_root, "tablib"));
        var (message, _) = KeptIndexes.Forget(id, Local, null, null);
        Assert.Equal(Strings.KeptUnknown(id), message);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void AnOldCopyHeldByAnOlderVersion_LeavesTheBaseWhole()
    {
        if (!OperatingSystem.IsWindows()) return;   // an open file can be removed elsewhere
        var old = MakeBase(Old, Tablib, Path.Combine(_root, "tablib"));
        var file = MakeBase(Local, Tablib, Path.Combine(_root, "tablib"));
        using var held = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var (message, heldId) = KeptIndexes.Forget(Tablib, Local, Old, null);

        Assert.Equal(Strings.KeptHeld, message);
        Assert.Equal(Tablib, heldId);
        Assert.True(File.Exists(file), "the local base went while the old one stayed: the next opening reads the old one");
    }

    [Fact]
    public void ABaseAServiceHolds_IsNeitherForgottenNorPruned_ThenGoesOnceLetGo()
    {
        if (!OperatingSystem.IsWindows()) return;   // nothing can hold a file elsewhere: the index is built again
        var file = MakeBase(Local, Tablib, Path.Combine(_root, "tablib"), opened: Now.AddDays(-200));
        var hold = IndexHome.Hold(file);

        Assert.Equal(Tablib, KeptIndexes.Forget(Tablib, Local, null, null).HeldId);
        Assert.Null(KeptIndexes.Prune(Local, null, null, Now));
        Assert.True(File.Exists(file));

        hold!.Dispose();
        Assert.Null(KeptIndexes.Forget(Tablib, Local, null, null).HeldId);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task AnIndexingService_HoldsItsBase_UntilItIsDisposed()
    {
        if (!OperatingSystem.IsWindows()) return;
        TestRagStore.Redirect();
        var root = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "Alpha.cs"), string.Join('\n',
            "public class Alpha", "{", "    public int One()   => 1;", "    public int Two()   => 2;", "    public int Three() => 3;",
            "    public int Four()  => 4;", "    public int Five()  => 5;", "    public int Six()   => 6;", "}"));
        var svc = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig { RagEnabled = true }, new LspSemanticProvider());
        svc.StartIndexing(root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!(svc.Status.StartsWith("RAG: ✅", StringComparison.Ordinal) && !svc.IsIndexing))
        {
            Assert.True(DateTime.UtcNow < deadline, $"the pass did not finish (status: {svc.Status})");
            await Task.Delay(50);
        }
        var id = Path.GetFileName(RagDatabase.PathFor(root));
        // The pool's idle connections say nothing: only the service's own hold may refuse.
        SqliteConnection.ClearAllPools();

        Assert.Equal(id, KeptIndexes.Forget(id, TestRagStore.Dir, null, null).HeldId);

        svc.Dispose();
        SqliteConnection.ClearAllPools();
        Assert.Null(KeptIndexes.Forget(id, TestRagStore.Dir, null, null).HeldId);
    }

    // ── What goes on its own ─────────────────────────────────────────

    [Fact]
    public void AnIndexNotOpenedFor90Days_Goes_AndSaysWhatCameBack()
    {
        var gone = MakeBase(Local, Tablib, Path.Combine(_root, "tablib"), opened: Now.AddDays(-91));
        var kept = MakeBase(Local, Shop, Path.Combine(_root, "shop"), opened: Now.AddDays(-89));

        var pruned = KeptIndexes.Prune(Local, null, null, Now);

        Assert.Equal(["tablib"], pruned!.Names);
        Assert.True(pruned.Bytes > 0);
        Assert.False(File.Exists(gone));
        Assert.False(File.Exists(gone + ".opened"));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void ABaseWithoutAMark_StartsItsClockHere_NeverGoesAtTheUpdate()
    {
        // After the update no base has a mark, and its date says when it was written: every quiet repository would go at once.
        var file = MakeBase(Local, Tablib, Path.Combine(_root, "tablib"));
        Assert.Null(KeptIndexes.Prune(Local, null, null, Now));
        Assert.True(File.Exists(file));
        Assert.DoesNotContain(Strings.KeptOpenedToday, Read().Rows[0].Detail, StringComparison.Ordinal);

        Assert.Null(KeptIndexes.Prune(Local, null, null, Now.AddDays(89)));
        Assert.Equal(["tablib"], KeptIndexes.Prune(Local, null, null, Now.AddDays(91))!.Names);
    }

    [Fact]
    public void AnOldCopyAnOlderVersionStillWrites_KeepsTheIndex()
    {
        // The local base has served nobody for months, but an older editor uses the old copy every day.
        var file = MakeBase(Local, Tablib, Path.Combine(_root, "tablib"), opened: Now.AddDays(-200));
        var old = MakeBase(Old, Tablib, Path.Combine(_root, "tablib"));
        File.SetLastWriteTimeUtc(old, Now.AddDays(-1));

        Assert.Null(KeptIndexes.Prune(Local, Old, null, Now));
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(old));

        File.SetLastWriteTimeUtc(old, Now.AddDays(-200));
        Assert.NotNull(KeptIndexes.Prune(Local, Old, null, Now));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void ARecentWrite_UnderAnOldMark_KeepsTheIndex()
    {
        var file = MakeBase(Local, Tablib, Path.Combine(_root, "tablib"), opened: Now.AddDays(-200));
        File.SetLastWriteTimeUtc(file, Now.AddDays(-1));
        Assert.Null(KeptIndexes.Prune(Local, null, null, Now));
    }

    [Fact]
    public void TheFolderOpenHere_NeverGoes_HoweverOld()
    {
        var file = MakeBase(Local, Shop, Path.Combine(_root, "shop"), opened: Now.AddDays(-400));
        Assert.Null(KeptIndexes.Prune(Local, null, Shop, Now));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void ABaseHeldOpen_Stays_AndIsNotSaid()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = MakeBase(Local, Tablib, Path.Combine(_root, "tablib"), opened: Now.AddDays(-200));
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Null(KeptIndexes.Prune(Local, null, null, Now));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void AMark_IsTouchedAtMostOnceAnHour()
    {
        var file = MakeBase(Local, Tablib, Path.Combine(_root, "tablib"), opened: Now);
        IndexHome.NoteOpened(file, Now.AddMinutes(30));
        Assert.Equal(Now, IndexHome.OpenedAt(file));
        IndexHome.NoteOpened(file, Now.AddMinutes(61));
        Assert.Equal(Now.AddMinutes(61), IndexHome.OpenedAt(file));
    }

    [Fact]
    public void OpeningABase_LeavesItsOwnDateAlone()
    {
        // Its date says when it was written, and decides what is copied again.
        var root = Path.Combine(_root, "repo");
        var db = new RagDatabase(root);
        var written = File.GetLastWriteTimeUtc(db.DbPath);
        Assert.NotNull(IndexHome.OpenedAt(db.DbPath));
        _ = new RagDatabase(root);
        Assert.Equal(written, File.GetLastWriteTimeUtc(db.DbPath));
    }

    // ── The notice ───────────────────────────────────────────────────

    [Fact]
    public void AnIndexRemoved_IsSaidInTheNotice_EvenWithNothingElseToSay()
    {
        var notice = AwayNotice.Build([], "vs", "Visual Studio", [], new HashSet<string>(), Now, null,
                                      new PrunedIndexes(["prototype", "demo-site"], 187_000_000));
        Assert.Equal(Strings.KeptPrunedTitle, notice!.Title);
        Assert.Contains("`demo-site`", notice.Pruned!.Text, StringComparison.Ordinal);
        Assert.Equal(Strings.KeptPrunedDetail, notice.Pruned.Detail);
    }

    [Fact]
    public void NothingRemoved_IsNoNotice()
    {
        Assert.Null(AwayNotice.Build([], "vs", "Visual Studio", [], new HashSet<string>(), Now));
        Assert.Null(AwayNotice.Build([], "vs", "Visual Studio", [], new HashSet<string>(), Now, null, new PrunedIndexes([], 0)));
    }

    // ── /index ───────────────────────────────────────────────────────

    private static string Index(params string[] parts)
    {
        using var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig(), new LspSemanticProvider());
        return IndexCommandHandler.Handle(index, new InferpalConfig(), ["/index", .. parts], null);
    }

    [Fact]
    public void AnUnknownSubCommand_SaysTheUsage_WithEveryWord()
    {
        var text = Index("forget");
        Assert.Contains("list", text, StringComparison.Ordinal);
        Assert.Contains("forget <name>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ForgettingANameNobodyHas_SaysSo()
    {
        Assert.Equal(Strings.KeptUnknown("no-such-repo"), Index("forget", "no-such-repo"));
    }

    [Fact]
    public void ANameWithSpaces_IsOneName()
    {
        Assert.Equal(Strings.KeptUnknown("my repo"), Index("forget", "my", "repo"));
    }

    [Fact]
    public void TheList_SaysEachIndex_ThePruningFirst_AndTheFolderOfTwoOfOneName()
    {
        MakeBase(Local, Shop, Path.Combine(_root, "a", "app"), opened: Now.AddDays(-2));
        MakeBase(Local, Tablib, Path.Combine(_root, "b", "app"), opened: Now.AddDays(-3));
        var text = KeptIndexes.List(Read(), new PrunedIndexes(["prototype"], 9_600_000));

        Assert.StartsWith(Strings.KeptPruned(1, KeptIndexes.Size(9_600_000), "`prototype`"), text, StringComparison.Ordinal);
        Assert.Contains($"`{Path.Combine(_root, "a", "app")}`", text, StringComparison.Ordinal);
        Assert.EndsWith(Strings.KeptListHint, text, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingKept_SaysSo_AndNoSizeOfNothing()
    {
        Assert.Equal(Strings.KeptEmpty, KeptIndexes.List(Read(), null));
        Assert.Equal("0 MB", KeptIndexes.Size(0));
    }

    [Fact]
    public void TwoFoldersOfOneName_AskForTheFolder_WhichForgetsOne()
    {
        var a = MakeBase(Local, Shop, Path.Combine(_root, "a", "app"));
        var b = MakeBase(Local, Tablib, Path.Combine(_root, "b", "app"));

        Assert.Equal(Strings.KeptAmbiguous("app"), KeptIndexes.ForgetByName("app", Local, null, null));
        Assert.True(File.Exists(a) && File.Exists(b));

        var withSlash = Path.Combine(_root, "b", "app") + Path.DirectorySeparatorChar;
        KeptIndexes.ForgetByName(withSlash, Local, null, null);
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(a));
    }

    [Theory]
    [InlineData("Inferpal.Host", "HostServer.cs")]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs")]
    public void TheNoticeOfAStart_Prunes(params string[] file)
    {
        var path = Path.Combine([ConventionCoverageTests.RepoRoot(), .. file]);
        Assert.Contains("KeptIndexes.PruneHere(", ConventionCoverageTests.CodeOnly(path), StringComparison.Ordinal);
    }

    [Fact]
    public void TheIndexCommand_ProtectsTheFolderTheIndexServes_NotTheCallersGuess()
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Core", "Services",
                                                                 "Commands", "IndexCommandHandler.cs"));
        Assert.Contains("index.RootDir is { Length: > 0 }", code, StringComparison.Ordinal);
    }
}
