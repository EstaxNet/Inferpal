using System.IO;
using System.Linq;
using Inferpal.Localization;
using Inferpal.Services.Tools;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>get_diagnostics</c> without a path builds one solution or project of several: it names, above the report, the
/// ones that build does not cover — and the verdict is still read under that note.
/// </summary>
/// <remarks>
/// "✓ Build successful (Lib.csproj)" reads as "the code compiles". With a test project beside it — the ordinary layout of
/// a folder opened in VS Code, where there is often no solution — the test project the agent had just edited was not
/// built, the verdict was Clean, and no end-of-turn notice fired.
/// </remarks>
public sealed class DiagnosticsNamesWhatItDidNotBuildTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"inferpal-diagnotbuilt-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Touch(string relative)
    {
        var path = Path.Combine(_root, Path.Combine(relative.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void WithoutASolution_TheOtherProjectsAreNamed()
    {
        Touch("src/Lib/Lib.csproj");
        Touch("tests/Lib.Tests/Lib.Tests.csproj");

        var built = GetDiagnosticsTool.FindProjectFile(_root)!;
        var note  = GetDiagnosticsTool.NotBuiltNote(_root, built);

        Assert.Equal("Lib.csproj", Path.GetFileName(built));
        Assert.Equal("(This build covered Lib.csproj and what it references. Not built: tests/Lib.Tests/Lib.Tests.csproj "
                   + "— pass 'path' to build one.)", note);
    }

    [Fact]
    public void WithASolution_OnlyTheOtherSolutionsAreNamed()
    {
        Touch("App.sln");
        Touch("samples/Demo/Demo.sln");
        Touch("src/X/X.csproj");   // in a solution: built with it, never named

        var built = GetDiagnosticsTool.FindProjectFile(_root)!;
        var note  = GetDiagnosticsTool.NotBuiltNote(_root, built);

        Assert.Equal("App.sln", Path.GetFileName(built));
        Assert.Equal("(This build covered App.sln. Not built: samples/Demo/Demo.sln — pass 'path' to build one.)", note);
    }

    [Fact]
    public void ASingleProject_HasNoNote()
    {
        // Reference arm: the ordinary workspace says nothing more.
        Touch("src/Lib/Lib.csproj");

        Assert.Null(GetDiagnosticsTool.NotBuiltNote(_root, GetDiagnosticsTool.FindProjectFile(_root)!));
    }

    [Fact]
    public void TheVerdict_IsReadUnderTheNote()
    {
        var note = "(This build covered Lib.csproj and what it references. Not built: tests/T.csproj — pass 'path' to build one.)";

        Assert.Equal(GetDiagnosticsTool.BuildVerdict.Clean, GetDiagnosticsTool.ReadVerdict(note + "\n\n" + Strings.DiagBuildOk("Lib.csproj")));
        Assert.Equal(GetDiagnosticsTool.BuildVerdict.Clean, GetDiagnosticsTool.ReadVerdict(Strings.DiagBuildOk("Lib.csproj")));   // witness
    }

    [Fact]
    public void TheNote_GoesAboveAReport_OnlyWhenTheToolChoseWhatToBuild()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Core", "Services", "Tools", "GetDiagnosticsTool.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
        var exec = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "ExecuteAsync").ToString();

        var chosen = exec.IndexOf("var chosen = path is null;", StringComparison.Ordinal);
        var find   = exec.IndexOf("path ??= FindProjectFile(root);", StringComparison.Ordinal);
        Assert.True(chosen > 0 && find > chosen, "the choice is not recorded before the project is found.");
        Assert.Contains("chosen && NotBuiltNote(root, path) is { } note ? note + \"\\n\\n\" + report : report", exec, StringComparison.Ordinal);
    }
}
