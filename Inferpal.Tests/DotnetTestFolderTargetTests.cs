using System.IO;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>run_tests</c> on a folder dotnet cannot resolve itself — no solution or project in it, or several — runs the one
/// <c>get_diagnostics</c> would build, and says which, and what it left out.
/// </summary>
/// <remarks>
/// <c>dotnet test &lt;folder&gt;</c> answers MSB1003 (none) or MSB1011 (several): nothing runs. The workspace root is such
/// a folder whenever the solution lives in <c>src/</c>, and it is what <c>/tdd</c> passes as <c>path</c> and what a call
/// without one runs in — the loop answered "nothing ran" round after round, in a repository full of tests.
/// </remarks>
public sealed class DotnetTestFolderTargetTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-dotnet-folder-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void Write(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Empty);
    }

    [Fact]
    public void ASolutionBelowTheRoot_IsRun_AndSaid()
    {
        Write("src/Shop.sln");
        Write("src/Shop/Shop.csproj");
        Write("src/Shop.Tests/Shop.Tests.csproj");

        var chosen = RunTestsTool.DotnetFolderTarget(_root);

        Assert.NotNull(chosen);
        Assert.Equal(Path.Combine(_root, "src", "Shop.sln"), chosen!.Value.Target);
        Assert.StartsWith("Note: ", chosen.Value.Note);                                // read under, as every note
        Assert.Contains("this run used src/Shop.sln", chosen.Value.Note);
        Assert.Equal("✓ 3 passed", RunTestsTool.WithoutNotes(chosen.Value.Note + "✓ 3 passed"));
    }

    [Fact]
    public void TwoSolutions_TheShallowestRuns_AndTheOtherIsNamed()
    {
        Write("Shop.sln");
        Write("Shop.slnx");
        Write("tools/Tools.sln");

        var chosen = RunTestsTool.DotnetFolderTarget(_root);

        Assert.NotNull(chosen);
        Assert.Equal(Path.Combine(_root, "Shop.sln"), chosen!.Value.Target);
        Assert.Contains("Not run: ", chosen.Value.Note);
        Assert.Contains("Shop.slnx", chosen.Value.Note);
        Assert.Contains("tools/Tools.sln", chosen.Value.Note);
    }

    [Fact]
    public void AFolderWithOneProject_IsLeftToDotnet()
    {
        // Reference arms: one solution or project in the folder is dotnet's to take; no .NET project anywhere is
        // dotnet's error to say.
        Write("Shop.sln");
        Write("src/Shop/Shop.csproj");
        Assert.Null(RunTestsTool.DotnetFolderTarget(_root));

        var empty = Path.Combine(_root, "docs");
        Directory.CreateDirectory(empty);
        Assert.Null(RunTestsTool.DotnetFolderTarget(empty));
    }

    [Fact]
    public async Task ARootWithItsProjectBelow_RunsIt_InsteadOfAFolderDotnetRefuses()
    {
        // The real SDK, on a project of its own framework: nothing to download.
        var lib = Directory.CreateDirectory(Path.Combine(_root, "src", "Lib")).FullName;
        File.WriteAllText(Path.Combine(lib, "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<TargetFramework>net$(BundledNETCoreAppTargetFrameworkVersion)</TargetFramework>"
            + "</PropertyGroup></Project>");

        using var args = System.Text.Json.JsonDocument.Parse("""{"runner":"dotnet"}""");
        var report = await new RunTestsTool(() => _root).ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.DoesNotContain("MSB1003", report);
        Assert.StartsWith("Note: ", report);
        Assert.Contains("this run used src/Lib/Lib.csproj", report);
    }

    [Fact]
    public void TheDotnetRunner_ResolvesAFolderBeforeRunningIt()
    {
        var source = ConventionCoverageTests.CodeOnly(
            Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Core", "Services", "Tools", "RunTestsTool.cs"));

        var runner = source[source.IndexOf("RunDotnetAsync(string workDir", StringComparison.Ordinal)..];
        runner = runner[..runner.IndexOf("DotnetTestArguments(path, filter)", StringComparison.Ordinal)];
        Assert.Contains("DotnetFolderTarget(path ?? workDir)", runner);
    }
}
