using System.Globalization;
using System.IO;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A limit query_data says to the model writes its numbers as the rest of the readers do, whatever the machine's culture:
/// never "1 000" (a narrow no-break space) on a French machine or "1.000" on a German one.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class ColumnarLimitCultureTests
{
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "columnar", name);
    }

    [Theory]
    [InlineData("large.lz4.feather")]
    public async Task ALimitSaidToTheModel_WritesItsNumbersInvariant_OnAFrenchMachine(string fixture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            var ex = await Assert.ThrowsAsync<DocumentLimitException>(() => ColumnarData.ReadAsync(Fixture(fixture), 1_000, 100, default));
            Assert.Contains("more than 1,000 values", ex.Message);
            Assert.DoesNotContain('\u202F', ex.Message);
            Assert.DoesNotContain('\u00A0', ex.Message);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
