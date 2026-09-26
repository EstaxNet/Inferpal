using System.IO;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/test</c> put the test file NEXT TO THE SOURCE. In C# that is the production project, which references no test
/// framework: the new <c>FooTests.cs</c> is compiled into it and the build breaks (<c>'Fact' could not be found</c>) —
/// and the <c>FooTests.cs</c> that already exists in the test project is never found, so a second one is created
/// instead of it being extended. Java has the same shape under Maven's <c>src/main/java</c>.
/// </summary>
public sealed class TestFileLocationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-testloc-").FullName;

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

    private const string TestProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="xunit" Version="2.9.0" />
            <ProjectReference Include="..\Lib\Lib.csproj" />
          </ItemGroup>
        </Project>
        """;

    private void DotnetSolution(string testsDir = "Lib.Tests", string projectReference = @"..\Lib\Lib.csproj")
    {
        Write("App.sln");
        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write($"{testsDir}/Lib.Tests.csproj", TestProject.Replace(@"..\Lib\Lib.csproj", projectReference));
    }

    [Fact]
    public void ACSharpSource_GetsItsTestInTheTestProject_MirroringItsFolder()
    {
        DotnetSolution();
        var source = Write("Lib/Services/Parser.cs");

        var test = TestFilePathResolver.Resolve(source);

        Assert.Equal(Path.Combine(_root, "Lib.Tests", "Services", "ParserTests.cs"), test);
    }

    [Fact]
    public void AnExistingTestFileInTheTestProject_IsTheOneExtended()
    {
        DotnetSolution();
        var source   = Write("Lib/Services/Parser.cs");
        var existing = Write("Lib.Tests/ParserTests.cs", "public class ParserTests { }");

        Assert.Equal(existing, TestFilePathResolver.Resolve(source));
    }

    [Fact]
    public void ASrcAndTestsLayout_IsFoundFromTheSolution()
    {
        Write("App.sln");
        Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write("tests/Lib.Tests/Lib.Tests.csproj", TestProject.Replace(@"..\Lib\Lib.csproj", @"..\..\src\Lib\Lib.csproj"));
        var source = Write("src/Lib/Parser.cs");

        Assert.Equal(Path.Combine(_root, "tests", "Lib.Tests", "ParserTests.cs"), TestFilePathResolver.Resolve(source));
    }

    [Fact]
    public void WithoutATestProject_TheTestGoesNextToTheSource()
    {
        // Reference arm: nothing to find, the previous convention.
        Write("App.sln");
        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        var source = Write("Lib/Parser.cs");

        Assert.Equal(Path.Combine(_root, "Lib", "ParserTests.cs"), TestFilePathResolver.Resolve(source));
    }

    [Fact]
    public void ASourceInsideATestProject_StaysThere()
    {
        // A helper of the test project is tested where it lives.
        DotnetSolution();
        var source = Write("Lib.Tests/Fakes/FakeClock.cs");

        Assert.Equal(Path.Combine(_root, "Lib.Tests", "Fakes", "FakeClockTests.cs"), TestFilePathResolver.Resolve(source));
    }

    [Fact]
    public void AMavenSource_GetsItsTestUnderSrcTestJava()
    {
        var source = Write("app/src/main/java/com/acme/Parser.java");

        Assert.Equal(Path.Combine(_root, "app", "src", "test", "java", "com", "acme", "ParserTest.java"),
                     TestFilePathResolver.Resolve(source));
    }
}
