using System.IO;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The build <c>get_diagnostics</c> chooses is the shallowest solution, whatever its format — a <c>.slnx</c> at the root
/// beats a <c>.sln</c> buried in a sub-folder.
/// </summary>
/// <remarks>
/// The formats were searched one after the other: any <c>.sln</c>, at any depth, before any <c>.slnx</c>. A repository
/// moved to <c>.slnx</c> with a side project's <c>tools/Tools.sln</c> had that side project built, and its verdict read
/// as the workspace's.
/// </remarks>
public sealed class RootSolutionFormatTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-slnx-root-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Write(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Empty);
        return full;
    }

    [Fact]
    public void ASlnxAtTheRoot_BeatsASlnInASubFolder()
    {
        var root = Write("Shop.slnx");
        Write(Path.Combine("tools", "Tools.sln"));

        Assert.Equal(root, GetDiagnosticsTool.FindProjectFile(_root));
    }

    [Fact]
    public void ASlnAtTheRoot_StillBeatsASlnxInASubFolder()
    {
        // Reference arm: depth decides, not the format.
        var root = Write("Shop.sln");
        Write(Path.Combine("tools", "Tools.slnx"));

        Assert.Equal(root, GetDiagnosticsTool.FindProjectFile(_root));
    }
}
