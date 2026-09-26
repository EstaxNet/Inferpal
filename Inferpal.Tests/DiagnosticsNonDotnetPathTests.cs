using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// get_diagnostics compiles .NET only, and ran <c>dotnet build</c> on whatever path it was given: on a package.json the
/// answer was "1 error(s) — package.json" with <c>package.json(1,1): error MSB4025</c> (measured) — an error located in
/// a file that is fine. And an existing folder, which <c>dotnet build</c> resolves itself, answered "file not found".
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares a localized message
public sealed class DiagnosticsNonDotnetPathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-diagpath-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task<string> RunAsync(string path)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { path }));
        return await new GetDiagnosticsTool(getRoot: () => _dir).ExecuteAsync(doc.RootElement, CancellationToken.None);
    }

    [Theory]
    [InlineData("package.json", """{ "name": "demo" }""")]
    [InlineData("pyproject.toml", "[project]\nname = \"demo\"\n")]
    public async Task ANonDotnetFile_IsRefused_NeverBuiltIntoAnErrorInsideIt(string name, string content)
    {
        File.WriteAllText(Path.Combine(_dir, name), content);

        var report = await RunAsync(name);

        Assert.StartsWith(GetDiagnosticsTool.NotADotnetProject, report);
        Assert.Contains("run_command", report);
        Assert.DoesNotContain("MSB4025", report);
        Assert.Equal(GetDiagnosticsTool.BuildVerdict.NotBuilt, GetDiagnosticsTool.ReadVerdict(report));
    }

    [Fact]
    public async Task AnExistingFolder_IsNotAFileNotFound()
    {
        var folder = Path.Combine(_dir, "App");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "App.csproj"), "<Project />");

        var report = await RunAsync("App");

        Assert.DoesNotContain(Strings.ToolFileNotFound(folder), report);
        Assert.DoesNotContain(GetDiagnosticsTool.NotADotnetProject, report);
    }

    [Fact]
    public async Task AProjectFile_IsNeverRefused()
    {
        // Reference arm: the refusal is about what the file IS, not about whether it builds.
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), "<Project />");

        var report = await RunAsync("App.csproj");

        Assert.DoesNotContain(GetDiagnosticsTool.NotADotnetProject, report);
    }
}
