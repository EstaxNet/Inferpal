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
        for (var i = 0; i < QueryDataTool.MaxRows + 50; i++) sb.Append(i).Append('\n');
        Write("many.csv", sb.ToString());

        var said = await Query("many.csv", "SELECT n FROM data");

        Assert.Contains($"more rows than the {QueryDataTool.MaxRows} shown", said);
    }

    [Fact]
    public async Task ASpreadsheet_IsNamed_WithWhatToDo()
    {
        Write("book.xlsx", "x");
        Assert.Contains("export it as CSV", await Query("book.xlsx", "SELECT 1"));
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
