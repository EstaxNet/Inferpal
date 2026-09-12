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
/// One folder the user cannot read stopped the whole workspace walk.
/// </summary>
/// <remarks>
/// A database volume mounted inside the repository (owned by the container's user, mode 700) on Linux
/// or macOS, a locked junction under a Windows profile: the recursive enumeration threw at the first
/// one. <c>list_files</c> answered "directory not found" about a directory that exists;
/// <c>search_in_files</c>, <c>analyze_impact</c> and <c>trace_dependency</c> threw; the index, the
/// symbol scan and <c>rename_symbol</c> went on with a silently partial list.
/// </remarks>
public sealed class InaccessibleFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"locked-{Guid.NewGuid():N}");
    private readonly string _locked;
    private FileSystemAccessRule? _deny;

    public InaccessibleFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "class A { }");
        _locked = Path.Combine(_root, "pgdata");
        Directory.CreateDirectory(_locked);
        File.WriteAllText(Path.Combine(_locked, "B.cs"), "class B { }");

        if (OperatingSystem.IsWindows())
        {
            var dir = new DirectoryInfo(_locked);
            var acl = dir.GetAccessControl();
            _deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.ListDirectory, AccessControlType.Deny);
            acl.AddAccessRule(_deny);
            dir.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(_locked, UnixFileMode.None);
        }
    }

    public void Dispose()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var dir = new DirectoryInfo(_locked);
                var acl = dir.GetAccessControl();
                if (_deny is not null) acl.RemoveAccessRule(_deny);
                dir.SetAccessControl(acl);
            }
            else
            {
                File.SetUnixFileMode(_locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // Witness: the folder really cannot be listed — otherwise nothing below measures anything.
    private void AssertTheLockHolds() =>
        Assert.ThrowsAny<UnauthorizedAccessException>(() => Directory.EnumerateFiles(_locked).ToList());

    [Fact]
    public void TheWalk_SkipsTheFolder_AndKeepsTheRest()
    {
        AssertTheLockHolds();

        var found = WorkspaceScan.EnumerateFiles(_root, "*.cs").ToList();

        Assert.Contains(found, f => Path.GetFileName(f) == "A.cs");
    }

    [Fact]
    public async Task ListFiles_ListsTheWorkspace_InsteadOfSayingItDoesNotExist()
    {
        AssertTheLockHolds();
        var tool = new ListFilesTool(() => _root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = _root }));

        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains("A.cs", result);
    }
}
