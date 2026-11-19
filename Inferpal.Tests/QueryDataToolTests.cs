using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>query_data</c>: SQL on a CSV, TSV or SQLite file of the workspace — read-only, no other file reachable, a deadline
/// on every query, the rows it does not show said.
/// </summary>
public sealed class QueryDataToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-query-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private Task<string> Query(string path, string sql) =>
        new QueryDataTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(new { path, sql }), default);

    private const string Orders =
        "id,date,status,amount,note\n" +
        "1,2026-11-01,paid,10.5,\"first, with a comma\"\n" +
        "2,2026-11-01,failed,20,\n" +
        "3,2026-11-02,failed,30.25,\"said \"\"late\"\"\"\n" +
        "4,2026-11-02,paid,40,ok\n";

    [Fact]
    public async Task ACsv_IsATable_WithTypedNumbers_AndQuotedFieldsKept()
    {
        Write("orders.csv", Orders);

        var count = await Query("orders.csv", "SELECT status, COUNT(*) AS n, SUM(amount) AS total FROM data GROUP BY status ORDER BY status");
        var quoted = await Query("orders.csv", "SELECT note FROM data WHERE id = 3");

        Assert.Contains("loaded as the table `data` (4 row(s))", count);
        Assert.Contains("failed,2,50.25", count);   // REAL summed, not concatenated text
        Assert.Contains("paid,2,50.5", count);
        Assert.Contains("said \"\"late\"\"", quoted);
    }

    [Fact]
    public async Task WholeNumbersBesideDecimals_AreOneNumericColumn()
    {
        Write("orders.csv", Orders);   // amount: 10.5, 20, 30.25, 40

        var said = await Query("orders.csv", "SELECT COUNT(*) FROM data WHERE amount > 5");

        Assert.Contains("amount REAL", said);
        Assert.Contains("\n4", said);   // compared as numbers ('10.5' > '5' would be false as text)
    }

    [Fact]
    public async Task ATextReadingNull_IsQuotedOnce_ABlobIsItsLength()
    {
        Write("n.csv", "v\nNULL\n");
        Assert.EndsWith("\"NULL\"", (await Query("n.csv", "SELECT v FROM data")).Trim());
        Assert.Contains("<1000 bytes>", await Query("n.csv", "SELECT zeroblob(1000)"));
    }

    [Fact]
    public async Task ASemicolonCsv_AndATsv_AreReadByTheirDelimiter()
    {
        Write("eu.csv", "name;amount\nA;1\nB;2\n");
        Write("t.tsv", "name\tamount\nA\t5\n");

        Assert.Contains("3", await Query("eu.csv", "SELECT SUM(amount) FROM data"));
        Assert.Contains("5", await Query("t.tsv", "SELECT amount FROM data WHERE name = 'A'"));
    }

    [Fact]
    public async Task ADatabase_IsQueried_ButNeverWritten()
    {
        var db = Path.Combine(_root, "app.db");
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE users (name TEXT); INSERT INTO users VALUES ('ada'), ('bob');";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(db);

        var read = await Query("app.db", "SELECT COUNT(*) FROM users");
        var write = await Query("app.db", "DELETE FROM users");

        Assert.Contains("2", read);
        Assert.StartsWith("Error:", write);
        Assert.Equal(before, File.ReadAllBytes(db));
    }

    [Fact]
    public async Task AnotherDatabase_CannotBeAttached()
    {
        Write("orders.csv", Orders);
        var elsewhere = Path.Combine(Path.GetTempPath(), "inferpal-elsewhere.db").Replace('\\', '/');

        var said = await Query("orders.csv", $"ATTACH '{elsewhere}' AS other");

        Assert.StartsWith("Error:", said);
        Assert.False(File.Exists(elsewhere));
    }

    [Fact]
    public async Task AQueryThatRunsForever_IsStoppedAtItsDeadline()
    {
        Write("orders.csv", Orders);
        var kept = QueryDataTool.Deadline;
        QueryDataTool.Deadline = TimeSpan.FromSeconds(1);
        try
        {
            var said = await Query("orders.csv", "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT COUNT(*) FROM c");
            Assert.Contains("was stopped", said);
        }
        finally { QueryDataTool.Deadline = kept; }
    }

    [Fact]
    public async Task MoreRowsThanShown_IsSaid()
    {
        var sb = new StringBuilder("n\n");
        for (var i = 0; i < 20_000; i++) sb.Append(i).Append('\n');
        Write("many.csv", sb.ToString());

        var said = await Query("many.csv", "SELECT n FROM data");

        // As many rows as the answer holds — far more than a fixed count would have shown of numbers this short.
        var shown = int.Parse(System.Text.RegularExpressions.Regex.Match(said, @"more rows than the (\d+) shown").Groups[1].Value);
        Assert.InRange(shown, 1000, 19_999);
        Assert.True(said.Length <= ToolOutputBudget.ListChars + 300, said.Length.ToString());
    }

    private string Fixture(string name)
    {
        var path = Path.Combine(_root, name);
        File.Copy(Path.Combine(WebviewRebuildTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "documents", name), path);
        return path;
    }

    /// <summary>The fixture (<c>docs/probes/pari110/build_corpus.py fixture</c>): a title merged over the columns, a blank
    /// row, the header, a region merged over three orders, Excel dates (one with a time), and a second sheet.</summary>
    [Fact]
    public async Task AWorkbook_IsATablePerSheet_MergedCellsFilled_DatesReadByDateFunctions()
    {
        Fixture("ventes-fusion.xlsx");

        var nord = await Query("ventes-fusion.xlsx", "SELECT COUNT(*) AS n FROM Ventes_2026 WHERE \"Région\" = 'Nord'");
        var march = await Query("ventes-fusion.xlsx", "SELECT COUNT(*) AS n FROM Ventes_2026 WHERE strftime('%Y-%m', Date) = '2026-03'");
        var time = await Query("ventes-fusion.xlsx", "SELECT Date FROM Ventes_2026 WHERE Client = 'Cy'");
        var join = await Query("ventes-fusion.xlsx", "SELECT pays FROM Clients WHERE nom = 'Bob'");

        Assert.Contains("sheet 'Ventes 2026' as the table Ventes_2026 (4 row(s)); columns: Région TEXT, Date TEXT, Client TEXT, Montant REAL.", nord);
        Assert.Contains("1 row(s) above the header left out", nord);
        Assert.Contains("1 merged range(s) filled", nord);
        Assert.EndsWith("\nn\n3", nord.ReplaceLineEndings("\n"));          // 1 without the fill
        Assert.EndsWith("\nn\n3", march.ReplaceLineEndings("\n"));         // 2026-03-02, -15, -31
        Assert.EndsWith("2026-04-01 14:30:00", time.Trim());
        Assert.EndsWith("BE", join.Trim());
    }

    [Fact]
    public async Task AJsonArray_AndAJsonlFile_AreTheTableData_NestedValuesAsJsonText()
    {
        Write("a.json", "[{\"id\": 1, \"ok\": true, \"tags\": [\"x\"]}, {\"id\": 2, \"ok\": false, \"extra\": \"late key\"}, 3]");
        Write("e.jsonl", "{\"level\":\"error\",\"user\":{\"id\":7}}\n\nnot json\n{\"level\":\"info\",\"user\":{\"id\":7},\"ms\":1.5}\r\n");

        var array = await Query("a.json", "SELECT SUM(ok), COUNT(extra) FROM data");
        var lines = await Query("e.jsonl", "SELECT COUNT(DISTINCT json_extract(user, '$.id')) AS users, SUM(ms) FROM data");

        Assert.Contains("(2 row(s)); columns: id INTEGER, ok INTEGER, tags TEXT, extra TEXT.", array);
        Assert.Contains("tags hold(s) JSON text", array);
        Assert.Contains("1 item(s) that are not objects were left out", array);
        Assert.EndsWith("\n1,1", array.ReplaceLineEndings("\n"));
        Assert.Contains("1 line(s) that are not valid JSON were left out", lines);
        Assert.EndsWith("\n1,1.5", lines.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task AKeywordSheet_IsAnUnquotedTable_AHeadingMergedDownIsNoValue_BooleansAreOneAndZero()
    {
        Fixture("ventes-fusion.xlsx");

        var said = await Query("ventes-fusion.xlsx", "SELECT COUNT(Notes) AS notes, SUM(\"Payé\") AS paid FROM Order_");

        Assert.Contains("sheet 'Order' as the table Order_ (3 row(s))", said);
        var guessed = await Query("ventes-fusion.xlsx", "SELECT COUNT(*) FROM [Sheet1$]");
        Assert.StartsWith("Error:", guessed);
        Assert.Contains("no such table", guessed);
        Assert.Contains("as the table Ventes_2026", guessed);   // the real names, with the error
        Assert.EndsWith("\nnotes,paid\n2,1", said.ReplaceLineEndings("\n"));   // "Notes" copied into row 2 would count 3
    }

    [Fact]
    public async Task AJsonWithAKeyPerLine_IsRefusedPastTheColumnLimit_BeforeItIsHeld()
    {
        Write("k.jsonl", string.Concat(Enumerable.Range(0, QueryDataTool.MaxColumns + 1).Select(i => $"{{\"k{i}\":1}}\n")));
        Write("page.xls", "<html><table><tr><td>1</td></tr></table></html>");

        Assert.Contains($"more than {QueryDataTool.MaxColumns} different keys", await Query("k.jsonl", "SELECT 1"));
        Assert.Contains("not an Excel workbook", await Query("page.xls", "SELECT 1"));
    }

    [Fact]
    public async Task AJsonThatIsNoArray_AndAnOpenDocumentSheet_SayWhy()
    {
        Write("o.json", "{\"a\": 1}");
        Write("b.ods", "x");

        Assert.Contains("not an array of objects", await Query("o.json", "SELECT 1"));
        Assert.Contains("OpenDocument spreadsheet", await Query("b.ods", "SELECT 1"));
    }

    [Theory]
    [InlineData("PRAGMA hard_heap_limit = 100000")]          // would starve every SQLite user of the process
    [InlineData("PRAGMA temp_store_directory = 'C:/x'")]     // process-wide, outside the workspace
    [InlineData("PRAGMA query_only = OFF")]
    [InlineData("CREATE TEMP TABLE t (x)")]
    [InlineData("VACUUM INTO 'C:/x.db'")]
    public async Task OnlyReadsPass_NoSettingNoWriteNoOtherFile(string sql)
    {
        Write("orders.csv", Orders);
        var said = await Query("orders.csv", sql);
        Assert.StartsWith("Error: query_data only reads", said);
    }

    [Fact]
    public async Task ASchemaPragma_Passes()
    {
        Write("orders.csv", Orders);
        Assert.Contains("amount", await Query("orders.csv", "PRAGMA table_info(data)"));
    }

    [Fact]
    public async Task AWalDatabase_LeavesNothingBesideIt_AndNothingLocked()
    {
        var db = Path.Combine(_root, "wal.db");
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t (x); INSERT INTO t VALUES (1), (2);";
            cmd.ExecuteNonQuery();
        }
        File.Delete(db + "-wal"); File.Delete(db + "-shm");

        Assert.Contains("2", await Query("wal.db", "SELECT COUNT(*) FROM t"));
        Assert.False(File.Exists(db + "-wal"));
        Assert.False(File.Exists(db + "-shm"));
        File.Delete(db);   // not locked by a pooled connection
    }

    [Fact]
    public async Task LeadingZeros_BigIdentifiers_AndNaN_StayText()
    {
        Write("codes.csv", "zip,id,score\n007,1234567890123456789012345,NaN\n75001,1,1.5\n");

        var said = await Query("codes.csv", "SELECT zip, id, typeof(score) FROM data ORDER BY rowid");

        Assert.Contains("zip TEXT, id TEXT, score TEXT", said);   // said in the loaded line
        Assert.Contains("007,1234567890123456789012345", said);
    }

    [Fact]
    public async Task ASemicolonCsv_ReadsDecimalCommas_AsNumbers()
    {
        Write("eu.csv", "name;amount\nA;1,5\nB;2,25\n");
        var said = await Query("eu.csv", "SELECT SUM(amount) FROM data");
        Assert.Contains("3.75", said);
        Assert.Contains("amount REAL", said);
    }

    [Fact]
    public async Task ExtraFields_AndADuplicateHeader_AreSaid_NotFailed()
    {
        Write("ragged.csv", "a,a,col2\n1,2,3,4\n5,6,7\n");
        var said = await Query("ragged.csv", "SELECT * FROM data");
        Assert.Contains("1 row(s) had more fields than the header", said);
        Assert.Contains("a_2", said);
    }

    [Fact]
    public void TheSqlText_IsNeverJudgedAsAPath_ByTheIgnoreFiles()
    {
        File.WriteAllText(Path.Combine(_root, ".aiignore"), "*secret*\n");
        Inferpal.Services.Governance.ContentExclusions.Declare(_root, () => Inferpal.Services.Governance.ContentExclusions.DefaultFiles);
        try
        {
            Write("orders.csv", Orders);
            var args = JsonSerializer.SerializeToElement(new { path = "orders.csv", sql = "SELECT secret_hash FROM data" });
            Assert.Null(Inferpal.Services.Execution.ToolRegistry.ExcludedNamedFile(args, _root));
            var named = JsonSerializer.SerializeToElement(new { path = "secret.csv", sql = "SELECT 1" });
            Write("secret.csv", "a\n1\n");
            Assert.NotNull(Inferpal.Services.Execution.ToolRegistry.ExcludedNamedFile(named, _root));   // witness: the gate runs
        }
        finally { Inferpal.Services.Governance.ContentExclusions.ForgetForTests(_root); }
    }

    [Fact]
    public async Task AFileOutsideTheWorkspace_IsRefused()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Query("../../x.csv", "SELECT 1"));
    }
}
