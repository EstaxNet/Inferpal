using System.IO;
using Inferpal.Services.Lsp;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>The language the workspace's C# compiles, read from its projects: what binds or renames reads at it.</summary>
public sealed class CSharpParsingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-parsing-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Project(string relative, string properties)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{properties}</PropertyGroup></Project>");
        return path;
    }

    [Theory]
    [InlineData("net8.0", LanguageVersion.CSharp12)]
    [InlineData("net8.0-windows", LanguageVersion.CSharp12)]
    [InlineData("net9.0", LanguageVersion.CSharp13)]
    [InlineData("net10.0", LanguageVersion.CSharp14)]
    [InlineData("net11.0", LanguageVersion.Preview)]
    [InlineData("netcoreapp3.1", LanguageVersion.CSharp8)]
    [InlineData("netstandard2.1", LanguageVersion.CSharp8)]
    [InlineData("netstandard2.0", LanguageVersion.CSharp7_3)]
    [InlineData("net472", LanguageVersion.CSharp7_3)]
    public void ATargetFramework_CompilesItsDefaultLanguage(string framework, LanguageVersion expected) =>
        Assert.Equal(expected, CSharpParsing.DefaultFor(framework));

    [Fact]
    public void AFrameworkNotRead_SaysNothing() => Assert.Null(CSharpParsing.DefaultFor("$(DefaultTfm)"));

    [Fact]
    public void ALangVersion_WinsOverTheFramework() =>
        Assert.Equal(LanguageVersion.CSharp14,
                     CSharpParsing.ProjectLanguage(Project("A/A.csproj", "<TargetFramework>net8.0</TargetFramework><LangVersion>14</LangVersion>")));

    [Fact]
    public void ALangVersionInADirectoryBuildPropsAbove_Applies()
    {
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"),
                          "<Project><PropertyGroup><LangVersion>preview</LangVersion></PropertyGroup></Project>");
        Assert.Equal(LanguageVersion.Preview,
                     CSharpParsing.ProjectLanguage(Project("src/A/A.csproj", "<TargetFramework>net8.0</TargetFramework>")));
    }

    [Fact]
    public void AParentProps_CountsOnlyWhenTheNearestImportsIt()
    {
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"),
                          "<Project><PropertyGroup><LangVersion>preview</LangVersion></PropertyGroup></Project>");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var nearest = Path.Combine(_root, "src", "Directory.Build.props");
        File.WriteAllText(nearest, "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>");
        var project = Project("src/A/A.csproj", "<TargetFramework>net8.0</TargetFramework>");

        Assert.Equal(LanguageVersion.CSharp12, CSharpParsing.ProjectLanguage(project));   // MSBuild stops at the nearest

        File.WriteAllText(nearest, "<Project><Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', "
                                   + "'$(MSBuildThisFileDirectory)../'))\" /></Project>");
        Assert.Equal(LanguageVersion.Preview, CSharpParsing.ProjectLanguage(project));
    }

    [Theory]
    [InlineData("<LangVersion Condition=\"'$(TargetFramework)' == 'net48'\">7.3</LangVersion>")]
    [InlineData("<LangVersion>$(MyLang)</LangVersion>")]
    public void ALangVersionNotReadableHere_StopsTheSearch_AndTheFrameworkDecides(string langVersion)
    {
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"),
                          "<Project><PropertyGroup><LangVersion>preview</LangVersion></PropertyGroup></Project>");
        Assert.Equal(LanguageVersion.CSharp12,
                     CSharpParsing.ProjectLanguage(Project("A/A.csproj", "<TargetFramework>net8.0</TargetFramework>" + langVersion)));
    }

    [Fact]
    public void TheSemanticIndex_ReadsAtItsProjectsLanguage_AndFollowsAProjectThatChanges()
    {
        var project = Project("App/App.csproj", "<TargetFramework>net8.0</TargetFramework>");
        File.WriteAllText(Path.Combine(_root, "App", "A.cs"), "class A { }\n");
        var index = CSharpSemanticIndex.ForWorkspace(_root);
        Assert.Equal(LanguageVersion.CSharp12, index.Parsing.LanguageVersion);

        Project("App/App.csproj", "<TargetFramework>net10.0</TargetFramework>");
        CSharpSemanticIndex.NotifyFileChanged(project);

        Assert.Equal(LanguageVersion.CSharp14, index.Parsing.LanguageVersion);
    }

    [Fact]
    public void AMultiTargetedProject_CompilesItsOldestLanguage_AndACommentedLangVersionIsNoneOfIt() =>
        Assert.Equal(LanguageVersion.CSharp12,
                     CSharpParsing.ProjectLanguage(Project("A/A.csproj",
                         "<TargetFrameworks>net10.0;net8.0</TargetFrameworks><!-- <LangVersion>preview</LangVersion> -->")));

    [Fact]
    public void TheWorkspace_ReadsAtItsNewestProject_AndAtTheNewestWithoutAny()
    {
        Assert.Equal(LanguageVersion.Preview, CSharpParsing.LanguageOf(_root));   // no project

        Project("Lib/Lib.csproj", "<TargetFramework>net8.0</TargetFramework>");
        Assert.Equal(LanguageVersion.CSharp12, CSharpParsing.LanguageOf(_root));

        Project("App/App.csproj", "<TargetFramework>net10.0</TargetFramework>");
        Assert.Equal(LanguageVersion.CSharp14, CSharpParsing.LanguageOf(_root));
    }
}
