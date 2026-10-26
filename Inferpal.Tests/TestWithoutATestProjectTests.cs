using System.IO;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/test</c> on a C# file whose project no test project references would write <c>FooTests.cs</c> beside the source:
/// in the production project, compiled into it — the build breaks on <c>'Fact' could not be found</c> — and run by
/// nobody, while the card promises "placed where your test runner finds them". It is refused, named, before the model
/// is asked.
/// </summary>
public sealed class TestWithoutATestProjectTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-notestproj-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Write(string relative, string content = "")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private const string Code = "public class Parser { public int Parse(string s) => int.Parse(s); }";

    private static async Task<(TestGenerationPlan Plan, bool Asked)> PlanAsync(string source)
    {
        var asked = false;
        var provider = new FakeInferenceProvider
        {
            OnChatRequest = (_, _, _, _) =>
            {
                asked = true;
                return Task.FromResult(new Inferpal.Models.ChatTurnResult("public class ParserTests { }", null, 0, 0));
            },
        };
        var plan = await TestGenerationPlanner.PlanAsync(provider, "m", source, Code, CancellationToken.None);
        return (plan, asked);
    }

    [Fact]
    public async Task AProjectNoTestProjectReferences_IsRefused_NamedAndBeforeTheModel()
    {
        Write("App.sln");
        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        var source = Write("Lib/Parser.cs", Code);

        var (plan, asked) = await PlanAsync(source);

        Assert.False(plan.Ok);
        Assert.False(asked);
        Assert.Contains("Lib", plan.Refusal);
        Assert.Contains("dotnet new xunit", plan.Refusal);
        Assert.Contains(_root, plan.Refusal);   // where the search looked
    }

    [Fact]
    public async Task WithATestProject_TheTestsAreWrittenThere()
    {
        // Reference arm: the same source, a test project that references its project.
        Write("App.sln");
        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write("Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.0" />
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        var source = Write("Lib/Parser.cs", Code);

        var (plan, asked) = await PlanAsync(source);

        Assert.True(asked);
        Assert.True(plan.Ok);
        Assert.Equal(Path.Combine(_root, "Lib.Tests", "ParserTests.cs"), plan.TestPath);
    }

    [Theory]
    [InlineData("Lib.Tests/Fakes/FakeClock.cs")]   // already in a test project
    [InlineData("scripts/Tool.cs")]                // in no project at all
    [InlineData("web/parser.ts")]                  // another language
    public async Task OtherSources_AreNotRefused(string relative)
    {
        Write("Lib.Tests/Lib.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit" Version="2.9.0" /></ItemGroup></Project>""");
        var source = Write(relative, Code);

        var (_, asked) = await PlanAsync(source);

        Assert.True(asked);
        Assert.Null(TestFilePathResolver.Locate(source).Missing);
    }

    /// <summary>A search that could not read a project file did not find "no test project": no cause is named on it, and
    /// nothing escapes — the previous placement stands.</summary>
    [Fact]
    public void AProjectFileThatCannotBeRead_NamesNoCause()
    {
        // FileShare.None is Windows' lock; elsewhere it is advisory and this arm proves nothing.
        if (!OperatingSystem.IsWindows()) return;
        Write("App.sln");
        var csproj = Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        var source = Write("Lib/Parser.cs", Code);

        using (new FileStream(csproj, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(TestFilePathResolver.Locate(source).Missing);
        // Reference arm: readable, the same layout is refused.
        Assert.Equal("Lib", TestFilePathResolver.Locate(source).Missing?.Project);
    }

    /// <summary>A test project whose packages come from a <c>Directory.Build.props</c> names no framework itself: its name
    /// says what it is, and the tests go there.</summary>
    [Fact]
    public void ATestProjectKnownByItsName_IsFound()
    {
        Write("App.sln");
        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write("Lib.Tests/Directory.Build.props", """<Project><ItemGroup><PackageReference Include="xunit" Version="2.9.0" /></ItemGroup></Project>""");
        Write("Lib.Tests/Lib.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup></Project>""");
        var source = Write("Lib/Parser.cs", Code);

        var (path, missing) = TestFilePathResolver.Locate(source);

        Assert.Null(missing);
        Assert.Equal(Path.Combine(_root, "Lib.Tests", "ParserTests.cs"), path);
    }

    /// <summary>Without a solution, src/ and tests/ side by side: the repository holds both, and is searched.</summary>
    [Fact]
    public void WithoutASolution_TheRepositoryIsSearched()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write("tests/Lib.Tests/Lib.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit" Version="2.9.0" /><ProjectReference Include="..\..\src\Lib\Lib.csproj" /></ItemGroup></Project>""");
        var source = Write("src/Lib/Parser.cs", Code);

        var (path, missing) = TestFilePathResolver.Locate(source);

        Assert.Null(missing);
        Assert.Equal(Path.Combine(_root, "tests", "Lib.Tests", "ParserTests.cs"), path);
    }
}
