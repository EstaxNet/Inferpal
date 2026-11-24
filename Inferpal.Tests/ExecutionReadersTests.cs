using System.IO;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Rules of the execution layer read the same way everywhere: a workspace at a drive root holds its files, and the files
/// that say where packages come from are known by every reader of that question.
/// </summary>
public sealed class ExecutionReadersTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-exec-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void AWorkspaceAtADriveRoot_AnchorsTheHistoryOfTheFoldersUnderIt()
    {
        // "R:\" (a subst drive) or "/": the root already ends with its separator, and "R:\\" + "\" matched nothing.
        var root = Path.GetPathRoot(_dir)!;
        var folder = Path.Combine(_dir, "src");
        Directory.CreateDirectory(folder);

        Assert.Equal(root, FileHistoryService.AnchorOfFolder(folder, root));
        // Reference arm: a workspace that is a folder anchors as before.
        Assert.Equal(_dir, FileHistoryService.AnchorOfFolder(folder, _dir));
    }

    [Theory]
    [InlineData("bunfig.toml")]
    [InlineData(".bunfig.toml")]
    [InlineData(".NPMRC")]
    [InlineData("Pip.Conf")]
    [InlineData(".npmrc")]   // reference arm
    public void AFileThatSaysWherePackagesComeFrom_IsOne_AsTheLookupReadsIt_InAnyCase(string name) =>
        Assert.True(PackageManifests.IsSourceConfiguration(name));

    [Fact]
    public void ATomlHeaderFollowedByATabAndAComment_IsStillTheHeader()
    {
        // A comment starts after any whitespace, as the lookup reads one: a tab before it left "[tool.uv.sources]\t# local"
        // no header, and the local package behind it was sent to PyPI as a public name.
        var provided = PackageManifests.ProvidedNames("pyproject.toml", "[tool.uv.sources]\t# local packages\nmylib = { path = \"../mylib\" }\n");
        Assert.Contains("mylib", provided);
    }
}
