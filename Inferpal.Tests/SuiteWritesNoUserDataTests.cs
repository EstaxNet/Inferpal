using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The suite writes nothing in the developer's profile: the code index and the debugger's test scaffold are redirected
/// before any test runs (<see cref="TestConfigIsolation"/>). Left to each test, one that forgets writes an empty index
/// database in <c>%AppData%\Inferpal\rag</c> at every run. Read from what the module initializer set, not from the
/// statics, which tests running in parallel move.
/// </summary>
public class SuiteWritesNoUserDataTests
{
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "inferpal-tests");

    [Fact]
    public void TheCodeIndex_IsUnderTheSuitesFolder() =>
        Assert.StartsWith(TestRoot, TestConfigIsolation.RagDirAtStart, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void TheTestScaffold_IsUnderTheSuitesFolder() =>
        Assert.StartsWith(TestRoot, TestConfigIsolation.ScaffoldDirAtStart, StringComparison.OrdinalIgnoreCase);
}
