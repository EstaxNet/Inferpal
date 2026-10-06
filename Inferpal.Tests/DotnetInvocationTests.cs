using System.IO;
using System.Text.Json;
using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  dotnet's command-line errors are not the code failing to compile.
//
//  Measured with the real SDK: in a folder holding two projects, `dotnet test` answers "error MSB1011: Specify which
//  project or solution file to use" — and run_tests reported "✗ BUILD FAILED — the code did not compile", which /tdd
//  reads as red and answers by editing sound code. And a folder given with a trailing separator, quoted into the
//  command line, escaped its own closing quote under Windows: "MSB1009 Project file does not exist" for a folder that
//  exists, reported the same way.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class DotnetInvocationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-dotnet-invocation-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // A project on the SDK's own framework: nothing to download, whatever SDK runs the suite.
    private const string Project =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
        + "<TargetFramework>net$(BundledNETCoreAppTargetFrameworkVersion)</TargetFramework>"
        + "</PropertyGroup></Project>";

    private async Task<string> RunAsync(object args)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return await new RunTestsTool(() => _root).ExecuteAsync(doc.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task TwoProjectsInTheFolder_IsACommandLineError_NotRed()
    {
        File.WriteAllText(Path.Combine(_root, "A.csproj"), Project);
        File.WriteAllText(Path.Combine(_root, "B.csproj"), Project);

        var report = await RunAsync(new { runner = "dotnet" });

        Assert.Contains("MSB1011", report);                                  // WITNESS: the real SDK said it
        Assert.StartsWith(RunTestsTool.DotnetCommandRejected, report);
        Assert.DoesNotContain(RunTestsTool.BuildFailed, report);
        Assert.True(TddCommandHandler.NothingRan(report));
    }

    [Fact]
    public async Task AFolderWithATrailingSeparator_ReachesDotnetIntact()
    {
        var lib = Directory.CreateDirectory(Path.Combine(_root, "Lib")).FullName;
        File.WriteAllText(Path.Combine(lib, "Lib.csproj"), Project);
        var path = lib + Path.DirectorySeparatorChar;
        Assert.True(Directory.Exists(path));                                 // WITNESS: the folder exists

        var report = await RunAsync(new { runner = "dotnet", path });

        Assert.DoesNotContain("MSB1009", report);
        Assert.DoesNotContain(RunTestsTool.DotnetCommandRejected, report);
    }

    [Fact]
    public void ACompilerError_StaysRed()
    {
        // Reference arm: a real compiler error is still the code failing to build, and an MSB1xxx mixed with one is too.
        var report = RunTestsTool.ParseDotnetOutput(
            "Foo.cs(3,5): error CS0103: The name 'x' does not exist in the current context [/p/Foo.csproj]\n", 1);
        Assert.StartsWith(RunTestsTool.BuildFailed, report);
        Assert.False(TddCommandHandler.NothingRan(report));
    }

    [Fact]
    public void TheCommandLine_IsAList_SoValuesAreNeverRequoted()
    {
        var args = RunTestsTool.DotnetTestArguments(@"C:\src\Tests\", "Name~\"quoted\"");

        Assert.Equal(["test", @"C:\src\Tests\", "--verbosity", "normal", "--nologo", "--filter", "Name~\"quoted\""], args);
    }
}
