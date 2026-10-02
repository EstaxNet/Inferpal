using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ "No .sln or .csproj file found. Provide the path parameter." names a remedy that exists only when a .NET project
/// is somewhere: in a JavaScript or Python workspace there is nothing for <c>get_diagnostics</c> to build, and a model
/// told to find a path ended its turn half the time. The answer now also names what works there.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares the localized sentence
public sealed class NoDotnetProjectRemedyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-nodotnet-").FullName;

    public NoDotnetProjectRemedyTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "package.json"), """{ "name": "shop", "scripts": { "test": "jest" } }""");
        File.WriteAllText(Path.Combine(_dir, "src", "cart.js"), "module.exports = { total: xs => xs.reduce((a, b) => a + b, 0) };\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task AWorkspaceWithNoDotnetProject_NamesTheRemedyThatWorksThere()
    {
        var answer = await new GetDiagnosticsTool(null, () => _dir)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.StartsWith(Strings.DiagNoProject, answer, StringComparison.Ordinal);   // witness: the no-project branch
        Assert.Contains("run_tests", answer, StringComparison.Ordinal);
        Assert.Contains("run_command", answer, StringComparison.Ordinal);
    }
}
