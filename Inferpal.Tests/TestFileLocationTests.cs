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
    public void TwoTestProjectsNamedAfterTheProject_TheSameOneIsPicked_OnEveryMachine()
    {
        // Both start with "Lib" and both reference it: a tie on the boolean key. The ordinal order puts "Lib.UnitTests"
        // (upper-case L) before "lib.Tests"; the file system (NTFS, case-insensitive) lists "lib.Tests" first.
        Write("App.sln");
        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write("lib.Tests/lib.Tests.csproj", TestProject);
        Write("Lib.UnitTests/Lib.UnitTests.csproj", TestProject);
        var source = Write("Lib/Parser.cs");

        Assert.Equal(Path.Combine(_root, "Lib.UnitTests", "ParserTests.cs"), TestFilePathResolver.Resolve(source));
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

    /// <summary>
    /// A NEW test file had to "infer the test framework from the source (e.g. xUnit for C#)" — but the source is
    /// production code and names no framework: in an NUnit or MSTest project the model wrote xUnit, and the file did
    /// not compile there either. The test project's own references say which.
    /// </summary>
    [Theory]
    [InlineData("NUnit", "NUnit")]
    [InlineData("MSTest.TestFramework", "MSTest")]
    [InlineData("xunit", "xUnit")]
    public async Task ANewTestFile_IsAskedInTheFrameworkTheTestProjectUses(string package, string framework)
    {
        Write("App.sln");
        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write("Lib.Tests/Lib.Tests.csproj", TestProject.Replace("xunit", package));
        var source = Write("Lib/Parser.cs", "public class Parser { public int Parse(string s) => int.Parse(s); }");

        List<Inferpal.Models.ChatMessageDto> sent = [];
        var provider = new FakeInferenceProvider
        {
            OnChatRequest = (_, messages, _, _) =>
            {
                sent = messages;
                return Task.FromResult(new Inferpal.Models.ChatTurnResult("public class ParserTests { }", null, 0, 0));
            },
        };

        await TestGenerationPlanner.PlanAsync(provider, "m", source, File.ReadAllText(source), CancellationToken.None);

        Assert.Contains($"The test project uses {framework}", string.Join("\n", sent.Select(m => m.Content)));
    }

    [Fact]
    public void AJsTestFile_IsWrittenForTheRunnerTheTestScriptUses()
    {
        Write("web/package.json", """{ "scripts": { "test": "vitest run" } }""");
        var source = Write("web/src/sum.ts", "export const sum = (a: number, b: number) => a + b;");

        Assert.Contains("The test project uses Vitest", TestGenerationPlanner.FrameworkLine(TestFilePathResolver.Resolve(source)));
    }

    [Fact]
    public void WhenNothingSaysTheFramework_NothingIsAdded()
    {
        // Reference arm: no project file anywhere — the instruction stays as it was.
        var source = Write("loose/parser.py", "def parse(s): return int(s)");

        Assert.Equal(string.Empty, TestGenerationPlanner.FrameworkLine(TestFilePathResolver.Resolve(source)));
    }

    [Fact]
    public void AMavenSource_GetsItsTestUnderSrcTestJava()
    {
        var source = Write("app/src/main/java/com/acme/Parser.java");

        Assert.Equal(Path.Combine(_root, "app", "src", "test", "java", "com", "acme", "ParserTest.java"),
                     TestFilePathResolver.Resolve(source));
    }

    // ── Python: where pytest collects ────────────────────────────────────────────
    //
    // "Next to the source" is collected only when pytest looks everywhere. A project that names its test folder —
    // `testpaths`, the common setup — never collects a test written beside the source: measured with pytest 9.1,
    // `testpaths = ["tests"]` runs tests/ and nothing in src/. The new test silently never runs.

    [Fact]
    public void APythonProjectWithTestpaths_GetsItsTestThere()
    {
        Write("pyproject.toml", "[project]\nname = \"shop\"\n\n[tool.pytest.ini_options]\ntestpaths = [\"tests\"]\npythonpath = [\"src\"]\n");
        Write("tests/test_existing.py");
        var source = Write("src/shop/cart.py", "def total(xs): return sum(xs)");

        Assert.Equal(Path.Combine(_root, "tests", "test_cart.py"), TestFilePathResolver.Resolve(source));
    }

    [Fact]
    public void APytestIniTestpath_IsFollowed_InItsSpaceSeparatedForm()
    {
        Write("pytest.ini", "[pytest]\ntestpaths = integration checks\n");
        Directory.CreateDirectory(Path.Combine(_root, "integration"));
        var source = Write("shop/cart.py");

        Assert.Equal(Path.Combine(_root, "integration", "test_cart.py"), TestFilePathResolver.Resolve(source));
    }

    [Fact]
    public void APythonProjectWithATestsFolder_GetsItsTestThere()
    {
        Write("setup.py", "from setuptools import setup\nsetup(name='shop')\n");
        Write("tests/test_existing.py");
        var source = Write("shop/cart.py");

        Assert.Equal(Path.Combine(_root, "tests", "test_cart.py"), TestFilePathResolver.Resolve(source));
    }

    [Fact]
    public void AnExistingPythonTestFile_IsTheOneExtended()
    {
        Write("pyproject.toml", "[tool.pytest.ini_options]\ntestpaths = [\"tests\"]\n");
        var existing = Write("tests/unit/test_cart.py", "def test_old(): pass");
        var source = Write("src/shop/cart.py");

        Assert.Equal(existing, TestFilePathResolver.Resolve(source));
    }

    // ── Rust: what cargo compiles ────────────────────────────────────────────────
    //
    // A file in src/ is compiled only when a `mod` declares it: the src/parser_test.rs /test wrote was compiled by
    // nobody — measured with cargo 1.98, `cargo test` stayed green with a failing test in it. Integration tests in
    // tests/ are found by cargo, and see the library's public API.

    [Fact]
    public void ARustLibrarySource_GetsItsTestInTheCratesTestsFolder()
    {
        Write("Cargo.toml", "[package]\nname = \"my-shop\"\nversion = \"0.1.0\"\n");
        Write("src/lib.rs", "pub mod parser;");
        var source = Write("src/parser.rs", "pub fn parse(s: &str) -> i32 { s.parse().unwrap() }");

        var testPath = TestFilePathResolver.Resolve(source);

        Assert.Equal(Path.Combine(_root, "tests", "parser_test.rs"), testPath);
        // It is its own crate: the model is told how to reach the code, and what it can see.
        Assert.Contains("use my_shop::", TestGenerationPlanner.FrameworkLine(testPath));
    }

    [Fact]
    public void ARustLibNameOverride_IsTheCrateToImport()
    {
        Write("Cargo.toml", "[package]\nname = \"my-shop\"\n\n[lib]\nname = \"shop\"\n");
        Write("src/lib.rs");
        var source = Write("src/cart.rs");

        Assert.Contains("use shop::", TestGenerationPlanner.FrameworkLine(TestFilePathResolver.Resolve(source)));
    }

    [Fact]
    public void AnExistingRustIntegrationTest_IsTheOneExtended()
    {
        Write("Cargo.toml", "[package]\nname = \"shop\"\n");
        Write("src/lib.rs");
        var existing = Write("tests/parsing/parser_test.rs", "#[test] fn old() {}");
        var source = Write("src/parser.rs");

        Assert.Equal(existing, TestFilePathResolver.Resolve(source));
    }

    [Fact]
    public void APythonProjectThatKeepsTestsBesideTheSource_StaysThatWay()
    {
        // Reference arm: no test folder named anywhere, none at the root — pytest looks everywhere.
        Write("pyproject.toml", "[project]\nname = \"shop\"\n");
        var source = Write("shop/cart.py");

        Assert.Equal(Path.Combine(_root, "shop", "test_cart.py"), TestFilePathResolver.Resolve(source));
    }
}
