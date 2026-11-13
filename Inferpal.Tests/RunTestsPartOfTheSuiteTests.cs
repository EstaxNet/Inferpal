using System.IO;
using System.Linq;
using Inferpal.Services.Agent;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A dotnet run_tests that ran one test project of the solution a run without <c>path</c> takes says which of that
/// solution's test projects did not run, so its green reads "part of the suite"; the root, the solution, the solution's
/// only test project — and a test project the whole run never builds — are no part.
/// </summary>
/// <remarks>⚠ Two wrong rules this guards against: "a path was given" (the root and the solution are paths, and
/// <c>/tdd</c> passes the root) and "every test project on disk" (samples, templates and vendored code hold test projects
/// no whole run builds, and the remedy "omit 'path'" would not run them).</remarks>
public sealed class RunTestsPartOfTheSuiteTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-part-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Project(string name, bool tests, string under = "")
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, under, name)).FullName;
        var file = Path.Combine(dir, name + ".csproj");
        File.WriteAllText(file, tests
            ? """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.0.0" /></ItemGroup></Project>"""
            : """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        return file;
    }

    /// <summary>A classic solution at the root listing <paramref name="projects"/>.</summary>
    private string Solution(params string[] projects)
    {
        var sln = Path.Combine(_root, "App.sln");
        File.WriteAllText(sln, string.Concat(projects.Select(p =>
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{Path.GetFileNameWithoutExtension(p)}\", "
            + $"\"{Path.GetRelativePath(_root, p)}\", \"{{11111111-1111-1111-1111-111111111111}}\"\nEndProject\n")));
        return sln;
    }

    private static bool IsPart(string? note) => RunTestsTool.RanPartOfTheSuite((note ?? "") + "✓ Tests passed (dotnet): 3 passed", null);

    [Fact]
    public void OneTestProjectOfTheSolution_IsPartOfTheSuite_AndNamesTheOthers()
    {
        var core = Project("Core.UnitTests", tests: true);
        var api  = Project("Api.IntegrationTests", tests: true);
        var lib  = Project("Lib", tests: false);
        Solution(core, api, lib);

        var note = RunTestsTool.OtherTestProjects(core, _root);

        Assert.NotNull(note);
        Assert.Contains("Api.IntegrationTests/Api.IntegrationTests.csproj", note);
        Assert.DoesNotContain("Lib.csproj", note);
        Assert.True(IsPart(note));
        // The project's folder, which dotnet reads as that project: the same.
        Assert.True(IsPart(RunTestsTool.OtherTestProjects(Path.GetDirectoryName(core), _root)));
    }

    [Fact]
    public void TheRoot_TheSolution_AndTheSolutionsOnlyTestProject_AreTheWholeSuite()
    {
        var only = Project("App.Tests", tests: true);
        var app  = Project("App", tests: false);
        var sln  = Solution(only, app);

        Assert.Null(RunTestsTool.OtherTestProjects(only, _root));
        Assert.Null(RunTestsTool.OtherTestProjects(_root, _root));
        Assert.Null(RunTestsTool.OtherTestProjects(sln, _root));
        Assert.Null(RunTestsTool.OtherTestProjects(null, _root));
    }

    /// <summary>A test project the solution does not list — a sample, a template, vendored code — is no part of the
    /// whole run: neither named, nor making the run of the solution's only test project a part.</summary>
    [Fact]
    public void ATestProjectOutsideTheSolution_IsNotCounted()
    {
        var tests = Project("App.Tests", tests: true);
        var app   = Project("App", tests: false);
        Project("Sample.Tests", tests: true, under: "samples");
        Solution(tests, app);

        Assert.Null(RunTestsTool.OtherTestProjects(tests, _root));
    }

    /// <summary>No solution for the whole run (one project at the root, or none): nothing to compare, nothing said.</summary>
    [Fact]
    public void WithoutASolutionForTheWholeRun_NothingIsSaid()
    {
        var a = Project("A.Tests", tests: true);
        Project("B.Tests", tests: true);

        Assert.Null(RunTestsTool.OtherTestProjects(a, _root));
    }

    /// <summary>The bar reads the same note: a green on one test project of the solution is "part of the suite".</summary>
    [Fact]
    public void TheBar_ReadsThePartAsTheToolSaidIt()
    {
        var core = Project("Core.Tests", tests: true);
        var web  = Project("Web.Tests", tests: true);
        Solution(core, web);
        var output = RunTestsTool.OtherTestProjects(core, _root) + "✓ Tests passed (dotnet): 4 passed";

        var verdict = ChatTurnPolicy.LastCheck([new ToolExecution("run_tests", $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(core)}}}""", output)]);

        Assert.True(verdict is { Part: true, Failed: false });
    }
}
