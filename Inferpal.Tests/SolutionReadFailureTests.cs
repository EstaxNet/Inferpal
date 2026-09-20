using System.IO;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A solution file nobody could read is not a solution with no project, and a project file nobody
/// could parse is not a project that declares nothing.
/// </summary>
/// <remarks>
/// <para>
/// Both collapse into the shape the reader already has a meaning for: <c>Projects : 0</c> is a fact
/// about the repository, and a project rendered with only its <c>File :</c> line is a project with
/// no target framework and no dependency. This report is what the workspace block hands the model
/// at the start of every session, and what it uses to decide where code belongs.
/// </para>
/// <para>
/// ⚠ The rule was already written, in the <c>catch</c> of <see cref="SolutionFiles"/> itself
/// ("an unreadable .slnx is not zero projects… conflating the two is exactly what this class exists
/// to correct") — held by a <c>Diagnostics</c> trace, which reaches a channel nobody opens while
/// the caller prints the number. A cause has to travel <b>with the result</b>.
/// </para>
/// </remarks>
[Collection(SignalCollection.Name)]
public sealed class SolutionReadFailureTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _base =
        Path.Combine(Path.GetTempPath(), $"inferpal-slnread-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { File.Delete(LastKnownSolutionFile.FilePath); } catch { }
        try { Directory.Delete(_base, recursive: true); } catch { }
        _scratch.Dispose();
    }

    private static JsonElement NoArgs() => JsonDocument.Parse("{}").RootElement;

    /// <summary>A workspace holding <c>App.slnx</c> plus, optionally, the project it names.</summary>
    private string Workspace(string name, string solution, string? project)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_base, name)).FullName;
        File.WriteAllText(Path.Combine(dir, "App.slnx"), solution);
        if (project is not null)
        {
            Directory.CreateDirectory(Path.Combine(dir, "App"));
            File.WriteAllText(Path.Combine(dir, "App", "App.csproj"), project);
        }
        return dir;
    }

    private const string OneProject = "<Solution><Project Path=\"App/App.csproj\" /></Solution>";

    private static Task<string> Report(string root) =>
        new GetSolutionInfoTool(new NullEditorSurface(), () => root)
            .ExecuteAsync(NoArgs(), CancellationToken.None);

    [Fact]
    public async Task AWellFormedSolution_StillReadsAsBefore()
    {
        // The reference arm: without it, every assertion below is satisfied by a tool that has
        // stopped reporting anything at all.
        var root = Workspace("ok", OneProject,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework>"
            + "</PropertyGroup><ItemGroup><PackageReference Include=\"Serilog\" /></ItemGroup></Project>");

        var report = await Report(root);

        Assert.Contains("Projects : 1", report, StringComparison.Ordinal);
        Assert.Contains("Framework : net8.0", report, StringComparison.Ordinal);
        Assert.Contains("Packages  : Serilog", report, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be read", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASolutionFileThatDoesNotParse_SaysSo_InsteadOfCountingZeroProjects()
    {
        // Valid enough to be found and named, not valid enough to be parsed.
        var root = Workspace("brokensln", "<Solution><Project Path=\"App/App.csproj\" />", project: null);

        var report = await Report(root);

        // WITNESS: the tool got as far as naming the solution — the assertion below is about what
        // it says of its contents, not about a report that never happened.
        Assert.Contains("Solution : App.slnx", report, StringComparison.Ordinal);

        Assert.Contains("Projects : unknown", report, StringComparison.Ordinal);
        Assert.Contains("the solution file could not be read", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectFileThatDoesNotParse_IsNamed_NotShownAsDeclaringNothing()
    {
        var root = Workspace("brokenproj", OneProject, "<Project Sdk=\"Microsoft.NET.Sdk\">");

        var report = await Report(root);

        // WITNESS: the solution itself read fine and the project is listed — the file is reached.
        Assert.Contains("Projects : 1", report, StringComparison.Ordinal);
        Assert.Contains("── App", report, StringComparison.Ordinal);

        Assert.Contains("[project file could not be read:", report, StringComparison.Ordinal);
        Assert.Contains("framework and references unknown", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectFileHeldByAnotherProcess_IsNamedToo()
    {
        // The parse error is one cause; a file that opens for nobody is the other, and it is the
        // ordinary one — a compiler or an editor holding the project while the model asks.
        var root = Workspace("locked", OneProject, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var path = Path.Combine(root, "App", "App.csproj");

        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        // WITNESS: the lock really denies the read — without it this test is green on a file that
        // parses, and measures nothing.
        Assert.Throws<IOException>(() => File.ReadAllText(path));

        var report = await Report(root);

        Assert.Contains("[project file could not be read:", report, StringComparison.Ordinal);
    }
}
