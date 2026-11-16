using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>msbuild_property</c>: a build setting's value and the file that set it, read from MSBuild's property tracking — the
/// cause a build error's message never names (pari 99).
/// </summary>
public sealed class MsbuildPropertyToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-msbuildprop-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void Write(string rel, string text)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    [Fact]
    public async Task AValueSetByAnImportedFile_IsTracedToItsFileAndLine()
    {
        // Its own Directory.Build.props stops MSBuild's walk up the folders: nothing of the machine's is read.
        Write("Directory.Build.props", "<Project>\n  <Import Project=\"build/legacy.props\" />\n</Project>\n");
        Write("build/legacy.props", "<Project>\n  <PropertyGroup>\n    <LangVersion>7.3</LangVersion>\n  </PropertyGroup>\n</Project>\n");
        Write("src/Shop/Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n</Project>\n");

        var said = await new MsbuildPropertyTool(null, () => _root)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { name = "LangVersion" }), default);

        Assert.Contains("LangVersion = \"7.3\" for src/Shop/Shop.csproj", said);
        Assert.Contains("set to \"7.3\" in build/legacy.props line 3", said);
    }

    private Task<string> Ask(string name) =>
        new MsbuildPropertyTool(null, () => _root).ExecuteAsync(JsonSerializer.SerializeToElement(new { name }), default);

    [Fact]
    public async Task AValueWrittenOverSeveralLines_IsRead()
    {
        Write("Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <NoWarn>CS1591;\n      CS1573</NoWarn>\n  </PropertyGroup>\n</Project>\n");
        Write("Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n</Project>\n");

        var said = await Ask("NoWarn");

        Assert.Contains("in Directory.Build.props line 3", said);
        Assert.Contains("CS1573", said);
    }

    [Fact]
    public async Task AMultiTargetedProject_IsReadForItsFirstFramework_AndSaysSo()
    {
        Write("Directory.Build.props", "<Project>\n  <PropertyGroup><LangVersion>7.3</LangVersion></PropertyGroup>\n</Project>\n");
        Write("Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks></PropertyGroup>\n" +
              "  <PropertyGroup Condition=\"'$(TargetFramework)' == 'net8.0'\"><LangVersion>12</LangVersion></PropertyGroup>\n</Project>\n");

        var said = await Ask("LangVersion");

        Assert.Contains("LangVersion = \"12\"", said);
        Assert.Contains("this is the build for net8.0", said);
    }

    [Fact]
    public async Task AFailedEvaluation_IsSaid_NeverReadAsNoFileSetsIt()
    {
        Write("Directory.Build.props", "<Project>\n  <Import Project=\"build/missing.props\" />\n</Project>\n");
        Write("Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n</Project>\n");

        var said = await Ask("LangVersion");

        Assert.Contains("failed", said);
        Assert.DoesNotContain("no file", said);
    }

    [Theory]
    [InlineData("Program.cs(4,9): error CS8641: 'else' cannot start a statement. [Shop.csproj]")]
    public void AnOrdinarySyntaxErrorInTheCS86Range_PointsNowhere(string line) => Assert.Null(GetDiagnosticsTool.SettingDecides(line));

    [Fact]
    public void UnderARestoreStop_ThePointerIsNotAdded()
    {
        var run = new Inferpal.Services.ChildProcessResult(1,
            "x.targets(266,5): error NETSDK1005: Assets file 'obj/project.assets.json' doesn't have a target for 'net8.0'. [Shop.csproj]", "", false);
        Assert.DoesNotContain("msbuild_property", GetDiagnosticsTool.Interpret(run, "Shop.csproj", 90));
    }

    [Fact]
    public void ANuGetPackagesBuildFile_IsNamedByItsPackage()
    {
        var file = Path.Combine(Path.GetTempPath(), "nuget", "packages", "acme.build", "1.0.0", "build", "Acme.Build.props");
        Assert.Equal("the NuGet package acme.build 1.0.0 (build/Acme.Build.props)", MsbuildPropertyTool.Where(file, _root));
    }

    [Fact]
    public void TheTrackingLines_AreRead_AsTheEvaluationWritesThem()
    {
        string[] log =
        [
            $@"  {_root}\Directory.Build.props(9,5): message : Property initial value: $(LangVersion)=""latest"" Source: {_root}\Directory.Build.props (9,5)",
            $@"  x : Property reassignment: $(LangVersion)=""7.3"" (previous value: ""latest"") at {_root}\build\legacy.props (3,5)",
        ];

        var report = MsbuildPropertyTool.Report("LangVersion", "Shop.csproj", log, _root);

        Assert.Contains("LangVersion = \"7.3\"", report);
        Assert.Contains("set to \"latest\" in Directory.Build.props line 9", report);
        Assert.Contains("changed to \"7.3\" in build/legacy.props line 3", report);
    }

    [Theory]
    [InlineData("CSC : error CS8630: Invalid 'nullable' value: 'Enable' for C# 7.3. [Shop.csproj]", "LangVersion")]
    [InlineData("CSC : error CS5001: Program does not contain a static 'Main' method [Shop.csproj]", "OutputType")]
    [InlineData("Customer.cs(5,19): error CS8618: Non-nullable property 'Name' must contain a non-null value [Shop.csproj]", "TreatWarningsAsErrors")]
    public void AnErrorABuildSettingDecides_PointsToTheTool(string line, string property)
    {
        var note = GetDiagnosticsTool.SettingDecides(line);
        Assert.Contains("msbuild_property", note);
        Assert.Contains(property, note);
    }

    [Fact]
    public void AnErrorInTheCode_PointsNowhere() =>
        Assert.Null(GetDiagnosticsTool.SettingDecides("Program.cs(5,38): error CS0246: The type or namespace name 'OrderService' could not be found"));

    [Fact]
    public void ThePointer_SitsUnderTheVerdict()
    {
        var run = new Inferpal.Services.ChildProcessResult(1, "CSC : error CS5001: Program does not contain a static 'Main' method [Shop.csproj]", "", false);
        var report = GetDiagnosticsTool.Interpret(run, "Shop.csproj", 90);
        Assert.Equal(GetDiagnosticsTool.BuildVerdict.Errors, GetDiagnosticsTool.ReadVerdict(report));
        Assert.EndsWith("outside the repository).", report);
    }
}
