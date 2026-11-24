using System.IO;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An index's database opens wherever its folder is, whatever its name holds: the path is a value of the connection,
/// never text pasted into it (a <c>;</c> in a folder name would start another keyword).
/// </summary>
public class IndexDatabasePathTests
{
    [Fact]
    public void AFolderWhoseNameHoldsASemicolon_StillOpens()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-a;b=c ").FullName;
        try
        {
            using var conn = SqliteBootstrapper.OpenIndex(Path.Combine(dir, "index.db"));
            Assert.True(File.Exists(Path.Combine(dir, "index.db")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* teardown never throws (TeardownDoesNotThrowTests) */ }
        }
    }
}
