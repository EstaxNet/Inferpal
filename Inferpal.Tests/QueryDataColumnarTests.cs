using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// query_data on Parquet and Arrow (pari 137): files written by pandas and pyarrow (Fixtures/columnar, made by
/// docs/probes/pari137/build_corpus.py) load as the table `data` with every type mapped to what SQLite reads — whatever
/// their compression.
/// </summary>
public sealed class QueryDataColumnarTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-columnar-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "columnar", name);
    }

    private async Task<string> Query(string fixture, string sql)
    {
        File.Copy(Fixture(fixture), Path.Combine(_root, fixture), overwrite: true);
        return await new QueryDataTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(new { path = fixture, sql }), default);
    }

    [Theory]
    [InlineData("small.snappy.parquet", "Parquet")]
    [InlineData("small.zstd.parquet", "Parquet")]
    [InlineData("small.lz4.feather", "Arrow")]
    [InlineData("small.zstd.feather", "Arrow")]
    [InlineData("small.plain.arrow", "Arrow")]
    public async Task EveryCompression_LoadsTheSameTable_WithItsTypesMapped(string fixture, string kind)
    {
        var said = await Query(fixture,
            "SELECT id, price, paid, day, at, kind, json_extract(tags, '$[0]') AS first_tag, json_extract(who, '$.name') AS name FROM data ORDER BY id");

        Assert.Contains($"({kind}) loaded as the table `data` (3 row(s))", said, StringComparison.Ordinal);
        Assert.Contains("id INTEGER", said, StringComparison.Ordinal);
        Assert.Contains("price REAL", said, StringComparison.Ordinal);
        Assert.Contains(",b,NULL,Bo", said, StringComparison.Ordinal);                // a category, an empty list, a structure
        Assert.Contains("2026-03-02 17:05:09", said, StringComparison.Ordinal);        // a timestamp as SQLite reads it
        Assert.Contains("1,9.5,1,2026-03-01,2026-03-01 08:30:00,a,x,Ana", said, StringComparison.Ordinal);   // every type, as SQLite reads it
        Assert.Contains("3,12.25,NULL,2026-03-03,2026-03-03 00:00:00,a,z,NULL", said, StringComparison.Ordinal);   // nulls
        Assert.Contains("json_extract", said, StringComparison.Ordinal);               // the hint about nested columns
    }

    [Theory]
    [InlineData("small.snappy.parquet")]
    [InlineData("small.lz4.feather")]
    public async Task NullsBooleansAndCategories_ReadAsSqliteValues(string fixture)
    {
        var said = await Query(fixture,
            "SELECT COUNT(*) FILTER (WHERE price IS NULL) AS no_price, SUM(paid) AS paid, COUNT(*) FILTER (WHERE kind = 'a') AS a, "
            + "COUNT(*) FILTER (WHERE date(day) = '2026-03-03') AS third FROM data");

        // One price missing, one paid (false is 0, null is not counted), two of kind a, one on the third of March.
        Assert.True(said.ReplaceLineEndings("\n").Contains("no_price,paid,a,third\n1,1,2,1", StringComparison.Ordinal), said);
    }

    [Fact]
    public async Task AFeatherPastOneLz4Block_Loads_ItsChainedBlocksDecoded()
    {
        // 20 000 rows: every column spans several 64 KB LZ4 blocks, linked as pyarrow writes them.
        var said = await Query("large.lz4.feather", "SELECT COUNT(*) AS n, SUM(id) AS ids, MAX(value) AS top FROM data WHERE \"group\" = 'g3'");

        Assert.True(said.ReplaceLineEndings("\n").Contains("n,ids,top\n2857,28567143,9997.5", StringComparison.Ordinal), said);
    }

    [Theory]
    [InlineData("edge.parquet")]
    [InlineData("edge.lz4.feather")]
    public async Task AnEmptyStringIsNoNull_ADoubleStaysReal_AndNaNInAListIsNull(string fixture)
    {
        var said = await Query(fixture,
            "SELECT COUNT(*) FILTER (WHERE name = '') AS empty, COUNT(*) FILTER (WHERE name IS NULL) AS nulls, "
            + "SUM(price) / 4 AS quarter, readings FROM data ORDER BY price LIMIT 1");

        Assert.Contains("price REAL", said, StringComparison.Ordinal);
        Assert.True(said.ReplaceLineEndings("\n").Contains("empty,nulls,quarter,readings\n1,1,15.0,", StringComparison.Ordinal)
                    || said.ReplaceLineEndings("\n").Contains("empty,nulls,quarter,readings\n1,1,15,", StringComparison.Ordinal), said);
        Assert.Contains("[1.5,null]", said, StringComparison.Ordinal);
        Assert.Contains("NaN or infinite were loaded as NULL", said, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("small.snappy.parquet")]
    [InlineData("small.lz4.feather")]
    public async Task AStructuresFields_AreColumnsToo_AndDatesAreSaidToBeText(string fixture)
    {
        var said = await Query(fixture, "SELECT COUNT(DISTINCT who_name) AS names, MAX(who_age) AS oldest FROM data");

        Assert.Contains("who_name TEXT", said, StringComparison.Ordinal);
        Assert.Contains("who_age INTEGER", said, StringComparison.Ordinal);
        Assert.True(said.ReplaceLineEndings("\n").Contains("names,oldest\n2,40", StringComparison.Ordinal), said);
        Assert.Contains("compare them as text or with date() and strftime(), never CAST", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatIsNoParquet_IsSaidSo()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "fake.parquet"), "id,name\n1,a\n");

        var said = await new QueryDataTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(new { path = "fake.parquet", sql = "SELECT 1" }), default);

        Assert.Contains("could not be loaded: it is not a Parquet file (no PAR1 mark)", said, StringComparison.Ordinal);
    }
}
