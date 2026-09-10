using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Issue #9: Visual Studio 2026 creates <c>.slnx</c> solutions by default, and Inferpal did not see
/// them — "No <c>.sln</c> file found", <c>Projects : 0</c>, and a wrong workspace root that made the
/// sandbox refuse <c>read_file</c>.
/// </summary>
/// <remarks>
/// ⚠ <b>The defect was NON-DETERMINISTIC</b>, which is why it survived: on Windows,
/// <c>Directory.GetFiles(dir, "*.sln")</c> <b>sometimes</b> returns <c>.slnx</c> files too, through
/// <b>8.3</b> short-name matching — and short-name generation is configured per volume. On the
/// maintainer's system volume discovery worked by accident; on the reporter's <c>G:\</c> it did not.
/// That is why the assertions below use an extension 8.3 also makes match (<c>.slnbak</c>): it is
/// the only way to measure that the pattern is no longer what decides.
/// </remarks>
public class SolutionFilesTests : IDisposable
{
    private readonly string _dir;

    public SolutionFilesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ob-slnx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    // ── Discovery ───────────────────────────────────────────────────────────

    [Fact]
    public void ASlnxIsFound_LikeASln()
    {
        Write("App.slnx", "<Solution />");

        Assert.True(SolutionFiles.DirectoryHasSolution(_dir));
        Assert.EndsWith("App.slnx", SolutionFiles.FirstIn(_dir));
    }

    [Fact]
    public void ANeighbourWithALongerExtension_IsNotASolution()
    {
        // ⚠ The test that actually measures the fix: "*.sln" catches "App.slnbak" through 8.3 on a
        // volume where short names are enabled. Explicit filtering never does — and it is the same
        // property that guarantees ".slnx" is found on EVERY volume.
        Write("App.slnbak", "not a solution");

        Assert.False(SolutionFiles.DirectoryHasSolution(_dir));
        Assert.Empty(SolutionFiles.FindIn(_dir));
    }

    [Fact]
    public void BothFormatsSideBySide_AreBothReturned()
    {
        Write("Legacy.sln", "");
        Write("Modern.slnx", "<Solution />");

        Assert.Equal(2, SolutionFiles.FindIn(_dir).Count);
    }

    [Fact]
    public void AnUnreadableDirectory_IsNotASolution_AndDoesNotThrow()
    {
        Assert.False(SolutionFiles.DirectoryHasSolution(Path.Combine(_dir, "nope")));
        Assert.Null(SolutionFiles.FirstIn(null));
    }

    [Theory]
    [InlineData("A.sln", true)]
    [InlineData("A.slnx", true)]
    [InlineData("A.SLNX", true)]
    [InlineData("A.slnbak", false)]
    [InlineData("A.csproj", false)]
    public void IsSolution_AnswersOnTheExtensionAlone(string name, bool expected) =>
        Assert.Equal(expected, SolutionFiles.IsSolution(name));

    // ── Parsing ─────────────────────────────────────────────────────────────

    [Fact]
    public void ASlnxProject_IsParsed_WhereTheClassicRegexFoundNothing()
    {
        // The exact case from the issue: the regex reading `Project("{GUID}") = ...` lines finds
        // nothing in XML, hence "Projects : 0" on a perfectly valid solution.
        var sln = Path.Combine(_dir, "LLMkonfiguracja.slnx");
        var xml = """
            <Solution>
              <Project Path="LLMkonfiguracja/LLMkonfiguracja.csproj" />
            </Solution>
            """;

        var projects = SolutionFiles.ParseProjects(sln, xml);

        var p = Assert.Single(projects);
        Assert.Equal("LLMkonfiguracja", p.Name);
        Assert.Equal(Path.Combine(_dir, "LLMkonfiguracja", "LLMkonfiguracja.csproj"), p.AbsolutePath);
    }

    [Fact]
    public void ASlnxProject_InsideFolders_IsFoundAtAnyDepth()
    {
        var sln = Path.Combine(_dir, "App.slnx");
        var xml = """
            <Solution>
              <Folder Name="/src/">
                <Folder Name="/src/lib/">
                  <Project Path="lib\Core.csproj" />
                </Folder>
              </Folder>
            </Solution>
            """;

        Assert.Equal("Core", Assert.Single(SolutionFiles.ParseProjects(sln, xml)).Name);
    }

    [Fact]
    public void AClassicSln_StillParses_AndSkipsSolutionFolders()
    {
        // WITNESS: the historical format must keep working unchanged — a fix that switched
        // everything to the XML parser would pass the .slnx tests above.
        var sln = Path.Combine(_dir, "Legacy.sln");
        var text =
            "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"Solution Items\", \"Solution Items\", \"{A}\"\r\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App\", \"App\\App.csproj\", \"{B}\"\r\n";

        var projects = SolutionFiles.ParseProjects(sln, text);

        Assert.Equal("App", Assert.Single(projects).Name);
    }

    [Fact]
    public void AMalformedSlnx_YieldsNoProject_WithoutThrowing()
    {
        // A failed read is not "zero projects", but it must not bring the tool down either: the
        // trace goes to Diagnostics and the caller returns an empty list.
        Assert.Empty(SolutionFiles.ParseProjects(Path.Combine(_dir, "Broken.slnx"), "<Solution"));
    }
}
