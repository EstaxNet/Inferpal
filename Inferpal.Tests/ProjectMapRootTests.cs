using System.IO;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The project map (<c>/map</c>, <c>generate_project_map</c>) is the WORKSPACE's.
/// </summary>
/// <remarks>
/// <see cref="ProjectMapService"/> received no root: after the VS signal and the open files, it looked
/// for a solution from the process's current directory — which, under Visual Studio, is never the
/// workspace — climbing eight levels. Under VS Code, in a workspace with no <c>.sln</c>, that climb
/// stopped at the first PARENT folder holding one: the model received another project's map, thinking
/// it was reading its own.
/// </remarks>
[Collection(SignalCollection.Name)]
public class ProjectMapRootTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _base =
        Path.Combine(Path.GetTempPath(), $"inferpal-maproot-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
        _scratch.Dispose();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public async Task TheMap_IsOfTheWorkspace_NotOfAParentSolutionOrOfTheProcess()
    {
        // A parent folder holding a solution, and below it a workspace that has none.
        var parent = Directory.CreateDirectory(Path.Combine(_base, "parent")).FullName;
        File.WriteAllText(Path.Combine(parent, "Parent.sln"), "Microsoft Visual Studio Solution File, Format Version 12.00\n");
        var root = Directory.CreateDirectory(Path.Combine(parent, "app")).FullName;
        File.WriteAllText(Path.Combine(root, "Probe.cs"), "namespace MapProbe;\npublic class Probe { }\n");

        var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig(), new LspSemanticProvider());
        index.SetRoot(root);

        var map = await new ProjectMapService(new NullEditorSurface(), index).GenerateMapAsync(CancellationToken.None);

        Assert.Contains($"Root   : {root}{Environment.NewLine}", map, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALegacyEncodedFile_IsMappedWithTheNamesItDeclares()
    {
        // Accented identifiers are C#; in a Windows-1252 file read raw as UTF-8, "Élève" became "\uFFFDl\uFFFDve" — a
        // name the model cannot search for.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var root = Directory.CreateDirectory(Path.Combine(_base, "legacy")).FullName;
        File.WriteAllBytes(Path.Combine(root, "Eleve.cs"),
            System.Text.Encoding.GetEncoding(1252).GetBytes("namespace École;\r\npublic class Élève { }\r\n"));
        var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig(), new LspSemanticProvider());
        index.SetRoot(root);

        var map = await new ProjectMapService(new NullEditorSurface(), index).GenerateMapAsync(CancellationToken.None);

        Assert.Contains("École", map, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFFFD", map, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("utf-16LE")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32LE")]
    public async Task AUnicodeFile_IsMappedWithTheNamesItDeclares(string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(_base, "unicode-" + name)).FullName;
        File.WriteAllBytes(Path.Combine(root, "User.cs"), UnicodeEncodedFilesTests.Saved(
            UnicodeEncodedFilesTests.EncodingNamed(name),
            "namespace 上层;\r\npublic interface I用户 { }\r\npublic class 用户 : I用户 { }\r\n"));
        var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig(), new LspSemanticProvider());
        index.SetRoot(root);

        var map = await new ProjectMapService(new NullEditorSurface(), index).GenerateMapAsync(CancellationToken.None);

        Assert.Contains("classes: 1  interfaces: 1", map, StringComparison.Ordinal);   // witness: both parsed
        Assert.Contains("上层", map, StringComparison.Ordinal);
        Assert.Contains("I用户", map, StringComparison.Ordinal);
        Assert.Contains("← 用户", map, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHost_GivesTheMapServiceTheIndex()
    {
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), "Inferpal.Host", "HostServer.cs"));

        // Witness: the service is still built there.
        Assert.Contains("new ProjectMapService(", host, StringComparison.Ordinal);

        Assert.Contains("new ProjectMapService(editor, index)", host, StringComparison.Ordinal);
    }
}
