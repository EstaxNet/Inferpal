using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A settings file the ignore files keep from the model stays kept from <c>query_database</c> too: its connection is not
/// read, and the refusal says which rule keeps it (pari 115).
/// </summary>
/// <remarks>Alone (<see cref="ContentExclusionSerialCollection"/>): a declared root changes what every other class reads.</remarks>
[Collection(ContentExclusionSerialCollection.Name)]
public sealed class QueryDatabaseExclusionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-db-exclude").FullName;

    public void Dispose()
    {
        ContentExclusions.ForgetForTests(_root);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task AnExcludedSettingsFile_IsNotRead_AndTheRuleIsNamed()
    {
        File.WriteAllText(Path.Combine(_root, "appsettings.Production.json"), "{ \"ConnectionStrings\": { \"Default\": \"Host=prod;Password=x\" } }");
        File.WriteAllText(Path.Combine(_root, "appsettings.Development.json"), "{ \"ConnectionStrings\": { \"Default\": \"Host=localhost\" } }");
        File.WriteAllText(Path.Combine(_root, ".aiignore"), "appsettings.Production.json\n");
        ContentExclusions.Declare(_root, () => ContentExclusions.DefaultFiles);

        var (kept, why) = await DatabaseSources.ResolveAsync("appsettings.Production.json#ConnectionStrings:Default", _root, CancellationToken.None);
        Assert.Null(kept);
        Assert.Contains(".aiignore", why);
        var (read, _) = await DatabaseSources.ResolveAsync("appsettings.Development.json#ConnectionStrings:Default", _root, CancellationToken.None);
        Assert.Equal("Host=localhost", read!.ConnectionString);                                       // WITNESS: the rule reads
    }
}
