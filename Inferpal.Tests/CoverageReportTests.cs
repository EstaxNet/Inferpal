using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What <c>/test coverage</c> reads without the model: a Cobertura report of any of the tools it runs, the targets it
/// takes from it, and the project facts it detects them with.
/// </summary>
public sealed class CoverageReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"cobertura-{Guid.NewGuid():N}");

    public CoverageReportTests() => Directory.CreateDirectory(Path.Combine(_root, "src", "pkg"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private CoberturaReport Parse(string classes, string? source = null) => CoberturaReport.Parse(
        $"<coverage><sources><source>{source ?? Path.Combine(_root, "src")}</source></sources><packages><package><classes>"
        + classes + "</classes></package></packages></coverage>", _root, File.Exists);

    private static string Class(string file, string lines, string methods = "") =>
        $"<class filename=\"{file}\"><methods>{methods}</methods><lines>{lines}</lines></class>";

    private static string Lines(params (int Line, int Hits)[] lines) =>
        string.Concat(lines.Select(l => $"<line number=\"{l.Line}\" hits=\"{l.Hits}\"/>"));

    [Fact]
    public void AFileNamedAgainstItsSourceFolder_IsReadRelativeToTheWorkspace()
    {
        File.WriteAllText(Path.Combine(_root, "src", "pkg", "core.py"), "x = 1\n");

        var report = Parse(Class("pkg/core.py", Lines((1, 1), (2, 0))));

        Assert.Equal(["src/pkg/core.py"], report.Files.Keys);
        Assert.Equal((1, 2), report.Of(["src/pkg/core.py"]));
    }

    [Theory]
    [InlineData("tests/test_core.py")]
    [InlineData("src/pkg/core_test.py")]
    [InlineData("src/App.Tests/FooTests.cs")]
    [InlineData("obj/Debug/App.g.cs")]
    [InlineData("node_modules/lib/index.js")]
    [InlineData("../outside/core.py")]
    public void TestsGeneratedThirdPartyAndOutsideFiles_AreNoTargets(string file)
    {
        var report = Parse(Class(file, Lines((1, 0), (2, 0), (3, 0))), source: _root);

        Assert.Empty(report.Files);
    }

    [Fact]
    public void AnIstanbulReport_WithItsDoctype_IsRead()
    {
        // jest, vitest and c8 all write this line first: refused, no Node project would ever be measured.
        var xml = "<?xml version=\"1.0\" ?>\n<!DOCTYPE coverage SYSTEM \"http://cobertura.sourceforge.net/xml/coverage-04.dtd\">\n"
                + $"<coverage><sources><source>{_root}</source></sources><packages><package><classes>"
                + Class("src/a.js", Lines((1, 1), (2, 0))) + "</classes></package></packages></coverage>";

        Assert.Equal((1, 2), CoberturaReport.Parse(xml, _root, File.Exists).Of(["src/a.js"]));
    }

    [Fact]
    public void ADoctypeIsSkipped_NeverResolved()
    {
        // An entity of the DTD is not expanded: the report fails to read rather than reading a file it names.
        var xml = "<!DOCTYPE coverage [<!ENTITY x SYSTEM \"file:///etc/hostname\">]>\n<coverage><sources><source>&x;</source></sources></coverage>";

        Assert.ThrowsAny<System.Xml.XmlException>(() => CoberturaReport.Parse(xml, _root, File.Exists));
    }

    [Fact]
    public void AFailedMeasure_ShowsTheBeginningOfItsOutput_WhereARunnerSaysWhy()
    {
        var output = "Startup Error: @vitest/coverage-v8 does not work with browser projects\n" + new string('s', 10_000) + "\nend";

        var shown = Services.Commands.TestCoverageCommandHandler.Ends(output, 1_200, 1_800);

        Assert.StartsWith("Startup Error", shown, StringComparison.Ordinal);
        Assert.EndsWith("end", shown, StringComparison.Ordinal);
        Assert.True(shown.Length < 3_100);
    }

    [Fact]
    public void TwoReports_AreAsCoveredAsTheBestOfThem()
    {
        var a = Parse(Class("src/a.cs", Lines((1, 1), (2, 0))), source: _root);
        var b = Parse(Class("src/a.cs", Lines((1, 0), (2, 3), (3, 0))), source: _root);

        var merged = CoberturaReport.Merge([a, b]);

        Assert.Equal((2, 3), merged.Of(["src/a.cs"]));
    }

    [Fact]
    public void Targets_AreTheMostUncoveredMethodsFirst_WithAtLeastTwoUncoveredLines()
    {
        var report = Parse(Class("src/Calc.cs", Lines(),
            Method("Add", (1, 1), (2, 0), (3, 0)) + Method("Sub", (5, 1), (6, 0), (7, 0), (8, 0)) + Method("Neg", (10, 1), (11, 0))),
            source: _root);

        var targets = CoverageTargets.Rank(report, string.Empty, _ => []);

        Assert.Equal(["Sub", "Add"], targets.Select(t => t.Name));
        Assert.Equal([6, 7, 8], targets[0].Uncovered);
    }

    private static string Method(string name, params (int Line, int Hits)[] lines) =>
        $"<method name=\"{name}\"><lines>{Lines(lines)}</lines></method>";

    [Fact]
    public void AMethodNamedByItsDeclarationLineOnly_RunsToTheNextOne()
    {
        // istanbul: each function is its declaration line; its body is in the class's lines.
        var report = Parse(Class("src/a.js", Lines((1, 1), (2, 0), (3, 0), (4, 1), (5, 0), (6, 0), (7, 0)),
                                 Method("first", (1, 1)) + Method("second", (4, 1))), source: _root);

        var targets = CoverageTargets.Rank(report, string.Empty, _ => []);

        Assert.Equal([("second", 4, 7), ("first", 1, 3)], targets.Select(t => (t.Name, t.First, t.Last)));
    }

    [Fact]
    public void APythonFunction_KeepsWhatItNests_AndAMethodIsItsOwnTarget()
    {
        string[] lines =
        [
            "def outer(x):",          // 1
            "    def inner(y):",      // 2
            "        return y",       // 3
            "    return inner(x)",    // 4
            "",                       // 5
            "class Box:",             // 6
            "    def get(self):",     // 7
            "        # comment",      // 8
            "        return 1",       // 9
            "    def put(self, v):",  // 10
            "        self.v = v",     // 11
        ];

        Assert.Equal([("outer", 1, 4), ("Box.get", 7, 9), ("Box.put", 10, 11)], CoverageTargets.PythonFunctions(lines));
    }

    [Theory]
    [InlineData("""{ "devDependencies": { "vitest": "^3" } }""", "Vitest")]
    [InlineData("""{ "dependencies": { "jest": "^30" } }""", "Jest")]
    [InlineData("""{ "scripts": { "test": "node --test test/" } }""", "NodeTest")]
    [InlineData("""{ "devDependencies": { "mocha": "^11" }, "scripts": { "test": "mocha" } }""", null)]
    [InlineData("""{ "devDependencies": [ "vitest" ] }""", null)]
    [InlineData("not json", null)]
    public void TheNodeRunner_IsTheOneThePackageDeclares(string packageJson, string? runner)
    {
        Assert.Equal(runner, NodeCoverage.RunnerOf(packageJson)?.ToString());
    }

    [Theory]
    [InlineData("""<PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" />""", true)]
    [InlineData("<PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>", true)]
    [InlineData("""<PackageReference Include="Serilog" Version="4.0.0" />""", false)]
    public void ATestProject_IsTheOneThatSaysIt(string project, bool isTest)
    {
        Assert.Equal(isTest, DotnetCoverage.IsTestProject(project));
    }

    [Fact]
    public void AProjectWithPytestCov_HasItSwitchedOff_OrItsCoverageReplacesOurs()
    {
        var withPlugin = new PytestCoverage(_root, string.Empty, "python", pytestCov: true);
        var without    = new PytestCoverage(_root, string.Empty, "python", pytestCov: false);

        Assert.Contains("--no-cov", withPlugin.PytestArgs("tests/test_a.py"));
        Assert.Contains("--no-cov", withPlugin.MeasureArgs(null));
        // Reference arm: without the plugin pytest would refuse an option it does not know.
        Assert.DoesNotContain("--no-cov", without.PytestArgs("tests/test_a.py"));
        Assert.Equal(["pytest", "-q", "-p", "no:cacheprovider", "tests/test_a.py"], without.PytestArgs("tests/test_a.py"));
    }

    [Fact]
    public void ThePytestMeasure_RunsInParallelMode_SoAProjectsParallelSettingCannotLeaveItWithoutData()
    {
        Assert.Contains("--parallel-mode", new PytestCoverage(_root, "src", "python", pytestCov: false).MeasureArgs(null));
    }

    [Fact]
    public void TheDotnetFramework_IsReadFromTheProject_WithItsTarget()
    {
        var project = Path.Combine(_root, "App.Tests.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net10.0;net8.0</TargetFrameworks></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit.v3" Version="3.0.0" /></ItemGroup>
            </Project>
            """);

        Assert.Equal("xUnit v3 (App.Tests, net10.0)", DotnetCoverage.Framework(project));
    }

    [Fact]
    public void TheDotnetFilter_IsTheNewFilesClasses()
    {
        var file = Path.Combine(_root, "CalcCoverageTests.cs");
        File.WriteAllText(file, "// Tests for the class under test.\npublic class CalcCoverageTests { }\ninternal class Helper { }\n");

        Assert.Equal("FullyQualifiedName~CalcCoverageTests|FullyQualifiedName~Helper", DotnetCoverage.Filter(file));
    }

    [Fact]
    public void ABlackFormattedSignature_AndAFlushLeftDocstring_StayInTheirFunction()
    {
        string[] lines =
        [
            "def load(",                 // 1
            "    path,",                 // 2
            "    strict=False,",         // 3
            ") -> dict:",                // 4
            "    \"\"\"Load it.",        // 5
            "",                          // 6
            "Flush-left text.",          // 7
            "\"\"\"",                    // 8
            "    return {}",             // 9
            "",                          // 10
            "def one(): return 1",       // 11
            "def two(x):",               // 12
            "    return x",              // 13
        ];

        Assert.Equal([("load", 1, 9), ("one", 11, 11), ("two", 12, 13)], CoverageTargets.PythonFunctions(lines));
    }

    [Fact]
    public void ACoverletOneLineMember_IsNotStretchedOverTheNextOne()
    {
        // coverlet gives each method its lines: a one-line member is one line, unlike istanbul's declarations.
        var report = Parse(Class("src/Store.cs", Lines((5, 0), (6, 0)),
            Method("Get", (5, 0)) + Method("Load", (7, 1), (8, 0), (9, 0))), source: _root);

        Assert.Equal(["Load"], CoverageTargets.Rank(report, string.Empty, _ => []).Select(t => t.Name));
    }

    [Fact]
    public void ANewNodeTest_TakesTheSuffixTheProjectsTestsHave()
    {
        Directory.CreateDirectory(Path.Combine(_root, "test"));
        File.WriteAllText(Path.Combine(_root, "test", "app.spec.ts"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "test", "user.spec.ts"), string.Empty);
        var node = new NodeCoverage(_root, string.Empty, NodeRunner.Jest, "jest.js", null);

        Assert.Equal(Path.Combine(_root, "test", "orders.coverage.spec.ts"), node.NewTestPath(Path.Combine(_root, "src", "orders.ts")));
    }

    [Theory]
    [InlineData("Vitest", "--coverage.enabled=false")]
    [InlineData("Jest", "--coverage=false")]
    public void ANodeTestFilesRun_LeavesTheProjectsCoverageOff(string runner, string flag)
    {
        var node = new NodeCoverage(_root, string.Empty, Enum.Parse<NodeRunner>(runner), "runner.js", "v8");

        Assert.Contains(flag, node.RunArgs("test/a.spec.ts"));
    }

    [Fact]
    public void APytestProjectWithXdist_RunsItsTestsInOurProcess()
    {
        Assert.Equal(["pytest", "-q", "-p", "no:cacheprovider", "-n", "0"],
                     new PytestCoverage(_root, string.Empty, "python", pytestCov: false, xdist: true).PytestArgs(null));
    }

    [Fact]
    public void AFolderInsideADotnetProject_IsDotnet_EvenWithAPackageJsonAtTheRoot()
    {
        Write("package.json", "{}");
        Write("Lib/Lib.csproj", "<Project />");
        var services = Path.Combine(_root, "Lib", "Services");
        Directory.CreateDirectory(services);

        Assert.True(CoverageEcosystems.NearestMarkerIsDotnet(services, _root));
        // Reference arm: a front end's folder, under the package.json alone.
        Directory.CreateDirectory(Path.Combine(_root, "web"));
        Assert.False(CoverageEcosystems.NearestMarkerIsDotnet(Path.Combine(_root, "web"), _root));
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private DotnetCoverage DotnetWorkspace(bool referencesTheSource)
    {
        Write("App.sln", string.Empty);
        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        var tests = Write("Lib.Tests/Lib.Tests.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" />
                {(referencesTheSource ? """<ProjectReference Include="..\Lib\Lib.csproj" />""" : string.Empty)}
              </ItemGroup>
            </Project>
            """);
        return new DotnetCoverage(_root, Path.Combine(_root, "App.sln"), [tests], null);
    }

    [Fact]
    public void ANewDotnetTest_GoesInTheTestProjectThatReferencesTheSource()
    {
        var source = Write("Lib/Services/Parser.cs", "class Parser { }");

        Assert.Equal(Path.Combine(_root, "Lib.Tests", "Services", "ParserCoverageTests.cs"),
                     DotnetWorkspace(referencesTheSource: true).NewTestPath(source));
    }

    [Fact]
    public void WithoutATestProjectThatReachesTheSource_TheNewDotnetTestHasNoPlace()
    {
        // Beside the source, it would be compiled into the code under test and run by nobody: the source itself is
        // returned, an existing file the command refuses before asking the model.
        var source = Write("Lib/Services/Parser.cs", "class Parser { }");

        Assert.Equal(source, DotnetWorkspace(referencesTheSource: false).NewTestPath(source));
    }
}
