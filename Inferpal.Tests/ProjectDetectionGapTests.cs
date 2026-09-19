using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Two tools DETECT what a workspace is by walking it, and both turn an invisible gap into a flat
/// negative: <c>run_tests</c> answers "No test runner detected", <c>get_diagnostics</c> answers
/// "No .sln or .csproj file found".
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A folder the walk cannot LIST leaves no trace in any total — its files are never enumerated —
/// so both answers are self-consistent and read as facts about the repository. They are facts about
/// what the process was allowed to see. The remedy they name makes it worse: "provide 'path'" and
/// "provide the path parameter" send the user to a file that is inside the folder nobody can open.
/// </para>
/// <para>
/// ⚠ This is the CLASS behind <see cref="UnlistableFolderCoverageTests"/>, which closed it for the
/// tools that REPORT a scan (analyze_impact, rename_symbol, the index, search_in_files, list_files,
/// the project map). The ones that merely DECIDE were left out — and deciding wrong is not softer
/// than reporting wrong: it picks another runner, or none.
/// </para>
/// <para>
/// The sentence is never written here: <c>WalkGap.Sentence()</c> owns the choice between "cannot be
/// listed" and "is a link, not followed", because the two send the reader to different places.
/// </para>
/// </remarks>
public sealed class ProjectDetectionGapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"detect-{Guid.NewGuid():N}");
    private readonly string _healthy = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"detect-ok-{Guid.NewGuid():N}");
    private readonly string _lockedDir;
    private FileSystemAccessRule? _deny;

    public ProjectDetectionGapTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "class Program { }");

        // The only marker of what this workspace is, inside a folder the process cannot list.
        _lockedDir = Path.Combine(_root, "pgdata");
        Directory.CreateDirectory(_lockedDir);
        File.WriteAllText(Path.Combine(_lockedDir, "App.sln"), "Microsoft Visual Studio Solution File");

        // The reference arm: the same shape, with nothing locked and no project either.
        Directory.CreateDirectory(Path.Combine(_healthy, "src"));
        File.WriteAllText(Path.Combine(_healthy, "src", "Program.cs"), "class Program { }");

        Lock();
    }

    public void Dispose()
    {
        Unlock();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_healthy, recursive: true); } catch { }
    }

    private void Lock()
    {
        if (OperatingSystem.IsWindows())
        {
            var dir = new DirectoryInfo(_lockedDir);
            var acl = dir.GetAccessControl();
            _deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.ListDirectory, AccessControlType.Deny);
            acl.AddAccessRule(_deny);
            dir.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(_lockedDir, UnixFileMode.None);
        }
    }

    private void Unlock()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var dir = new DirectoryInfo(_lockedDir);
                var acl = dir.GetAccessControl();
                if (_deny is not null) { acl.RemoveAccessRule(_deny); _deny = null; }
                dir.SetAccessControl(acl);
            }
            else
            {
                File.SetUnixFileMode(_lockedDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch { }
    }

    /// <summary>
    /// The witness, and it has two halves: the walk really misses the solution, and the detector
    /// really sees the folder. Without the first the test measures nothing; without the second it
    /// would be asking for a sentence nobody could produce.
    /// </summary>
    private void AssertTheProjectIsReallyOutOfSight()
    {
        Assert.Empty(WorkspaceScan.EnumerateFiles(_root, "*.sln"));
        Assert.NotNull(WorkspaceScan.FirstWalkGap(_root, _root));
        Assert.Equal("pgdata", WorkspaceScan.FirstWalkGap(_root, _root)!.Value.Folder);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ── run_tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunTests_WhenTheProjectIsInAnUnlistableFolder_NamesTheFolder()
    {
        AssertTheProjectIsReallyOutOfSight();

        var answer = await new RunTestsTool(() => _root).ExecuteAsync(Args("{}"), CancellationToken.None);

        // The remedy it already gives stays: the gap is a CAUSE added to it, not a replacement.
        Assert.Contains("No test runner detected", answer, StringComparison.Ordinal);
        Assert.Contains(Strings.ScanFolderSkipped("pgdata"), answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reference arm: a workspace with no project and nothing locked says what it always said, and
    /// not a word about a folder. Without it, a tool that printed the sentence unconditionally
    /// would pass the test above.
    /// </summary>
    [Fact]
    public async Task RunTests_OnAHealthyWorkspaceWithNoProject_SaysNothingAboutAFolder()
    {
        var answer = await new RunTestsTool(() => _healthy).ExecuteAsync(Args("{}"), CancellationToken.None);

        Assert.Contains("No test runner detected", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("pgdata", answer, StringComparison.Ordinal);
        Assert.Null(WorkspaceScan.FirstWalkGap(_healthy, _healthy));
    }

    // ── get_diagnostics ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetDiagnostics_WhenTheProjectIsInAnUnlistableFolder_NamesTheFolder()
    {
        AssertTheProjectIsReallyOutOfSight();

        var answer = await new GetDiagnosticsTool(null, () => _root)
            .ExecuteAsync(Args("{}"), CancellationToken.None);

        Assert.Contains(Strings.DiagNoProject, answer, StringComparison.Ordinal);
        Assert.Contains(Strings.ScanFolderSkipped("pgdata"), answer, StringComparison.Ordinal);
    }

    /// <summary>Reference arm, same role as above.</summary>
    [Fact]
    public async Task GetDiagnostics_OnAHealthyWorkspaceWithNoProject_SaysNothingAboutAFolder()
    {
        var answer = await new GetDiagnosticsTool(null, () => _healthy)
            .ExecuteAsync(Args("{}"), CancellationToken.None);

        Assert.Equal(Strings.DiagNoProject, answer);
    }
}
