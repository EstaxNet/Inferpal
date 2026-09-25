using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A folder the walk cannot <b>list</b> is one step worse than a file it cannot read: its files are
/// never even enumerated, so the coverage arithmetic is self-consistent and claims a complete scan.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Measured</b>: a workspace whose only dependant lives inside an unlistable folder. The walk
/// saw <b>one</b> file of two, its <c>failed</c> flag was <c>false</c> — that flag only ever meant
/// "the START directory could not be opened" — and <c>analyze_impact</c> answered
/// <c>Direct dependants (0) · Risk: LOW</c>. The previous round's <see cref="ScanCoverage.Unreadable"/>
/// cannot express this: the file was never <i>taken</i>, so the count is 0 and
/// <c>Total</c> is 1 — "1 of 1, complete".
/// </para>
/// <para>
/// ⚠ <see cref="InaccessibleFolderTests"/> closed the <b>crash</b> this same shape used to cause
/// (<c>list_files</c> answering "directory not found", the tools throwing) and its own remark names
/// the silence that was left: <i>"the index, the symbol scan and rename_symbol went on with a
/// silently partial list"</i>. A comment describing a half-fixed defect is a search query.
/// </para>
/// <para>
/// The real cases are the ones that remark lists: a database volume mounted inside the repository
/// and owned by a container's user (mode 700), a locked junction under a Windows profile.
/// </para>
/// </remarks>
public sealed class UnlistableFolderCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"unlist-{Guid.NewGuid():N}");
    private readonly string _lockedDir;
    private FileSystemAccessRule? _deny;

    public UnlistableFolderCoverageTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Target.cs"), """
            namespace App;
            public class Target
            {
                public void HandleTask() { }
            }
            """);
        // The only dependant, inside a folder the process will not be able to list.
        _lockedDir = Path.Combine(_root, "pgdata");
        Directory.CreateDirectory(_lockedDir);
        File.WriteAllText(Path.Combine(_lockedDir, "Caller.cs"), """
            namespace App.Host;
            using App;
            public class Caller
            {
                public void Route() { new Target().HandleTask(); }
            }
            """);

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

    public void Dispose()
    {
        Unlock();
        try { Directory.Delete(_root, recursive: true); } catch { }
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
    /// Both halves of the setup. The second one is what a platform without enforceable ACLs — or a
    /// test run as root — would silently drop, turning every assertion below into a green that
    /// measures nothing.
    /// </summary>
    private void AssertTheFixtureDiscriminates()
    {
        Assert.ThrowsAny<UnauthorizedAccessException>(() => Directory.EnumerateFiles(_lockedDir).ToList());

        var walk = WorkspaceScan.EnumerateFiles(_root, "*.cs", out var failed).ToList();
        Assert.False(failed);                                   // the walk believes it succeeded
        Assert.DoesNotContain(walk, f => Path.GetFileName(f) == "Caller.cs");
        Assert.Contains(walk, f => Path.GetFileName(f) == "Target.cs");
    }

    private string TargetPath => Path.Combine(_root, "src", "Target.cs");

    [Fact]
    public void TheWalkFunnel_NamesTheFolderItCouldNotList()
    {
        AssertTheFixtureDiscriminates();

        var gap = WorkspaceScan.FirstWalkGap(_root, _root);

        Assert.NotNull(gap);
        Assert.Equal("pgdata", gap!.Value.Folder);
        // The CAUSE matters as much as the folder: "unlistable" and "link not followed" send
        // the reader to two different places.
        Assert.Equal(WorkspaceScan.WalkGapKind.Unlistable, gap.Value.Kind);
    }

    [Fact]
    public void TheWalkFunnel_OnAFullyListableTree_NamesNothing()
    {
        // NEGATIVE WITNESS: without it, a detector that always returned a folder would pass
        // the test above while measuring nothing.
        Unlock();
        Directory.EnumerateFiles(_lockedDir).ToList();   // witness: the lock really is lifted

        Assert.Null(WorkspaceScan.FirstWalkGap(_root, _root));
    }

    [Fact]
    public void TheWalkFunnel_IgnoresAnExcludedFolderItCannotList()
    {
        // ⚠ A `node_modules` with odd permissions must NOT be reported: it is excluded anyway, and
        // a gate whose output is noise ends up disarmed. This case is what decided the detector's
        // shape — walking by hand rather than with `RecurseSubdirectories`, so that
        // `IsExcludedDirName` is honoured.
        Unlock();
        var excluded = Path.Combine(_root, "node_modules");
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(excluded, "dep.cs"), "class Dep { }");
        FileSystemAccessRule? deny = null;
        if (OperatingSystem.IsWindows())
        {
            var dir = new DirectoryInfo(excluded);
            var acl = dir.GetAccessControl();
            deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.ListDirectory, AccessControlType.Deny);
            acl.AddAccessRule(deny);
            dir.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(excluded, UnixFileMode.None);
        }
        try
        {
            // Witness: the lock on node_modules really holds.
            Assert.ThrowsAny<UnauthorizedAccessException>(
                () => Directory.EnumerateFiles(excluded).ToList());

            Assert.Null(WorkspaceScan.FirstWalkGap(_root, _root));
        }
        finally
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var dir = new DirectoryInfo(excluded);
                    var acl = dir.GetAccessControl();
                    if (deny is not null) acl.RemoveAccessRule(deny);
                    dir.SetAccessControl(acl);
                }
                else
                {
                    File.SetUnixFileMode(excluded,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            catch { }
        }
    }

    [Fact]
    public async Task AnalyzeImpact_WithAnUnlistableFolder_SaysSoInsteadOfClaimingCompleteness()
    {
        AssertTheFixtureDiscriminates();

        var tool = new AnalyzeImpactTool(() => _root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = TargetPath }));

        var report = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        // Positive assertion on the localized sentence: asserting the absence of "safe to refactor
        // freely" would also pass on a build where the whole verdict had disappeared.
        Assert.Contains(Inferpal.Localization.Strings.ScanFolderSkipped("pgdata"), report);
        Assert.Contains("Target", report);   // witness: this really is the report
    }

    [Fact]
    public async Task TraceDependency_WithAnUnlistableFolder_SaysSo()
    {
        AssertTheFixtureDiscriminates();

        var tool = new TraceDependencyTool(() => _root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(
            new { path = TargetPath, direction = "callers" }));

        var report = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains("HandleTask", report);   // witness: the target really was analysed
        Assert.Contains(Inferpal.Localization.Strings.ScanFolderSkipped("pgdata"), report);
    }

    [Fact]
    public async Task AnalyzeImpact_OnAFullyListableTree_SaysNothingAboutSkippedFolders()
    {
        Unlock();
        Directory.EnumerateFiles(_lockedDir).ToList();   // witness: the lock really is lifted

        var tool = new AnalyzeImpactTool(() => _root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = TargetPath }));

        var report = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.DoesNotContain(Inferpal.Localization.Strings.ScanFolderSkipped("pgdata"), report);
        // The other half of the witness: the dependant is now SEEN, so the report is not simply
        // empty for some other reason.
        Assert.Contains("Caller", report);
    }
}
