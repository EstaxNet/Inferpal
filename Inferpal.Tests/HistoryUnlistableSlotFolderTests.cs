#if WINDOWS
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A folder of auto-save slots that cannot be listed is a gap in the search, said as one: thrown out of the search, it
/// lost every saved session already found. Windows only: a deny rule is what the test can set; a POSIX mode is ignored by
/// a container's root.
/// </summary>
public sealed class HistoryUnlistableSlotFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"history-unlistable-{Guid.NewGuid():N}");
    private FileSystemAccessRule? _deny;

    private string SlotDir => Path.Combine(_dir, "autosave");

    public void Dispose()
    {
        try
        {
            if (_deny is not null)
            {
                var dir = new DirectoryInfo(SlotDir);
                var acl = dir.GetAccessControl();
                acl.RemoveAccessRule(_deny);
                dir.SetAccessControl(acl);
            }
        }
        catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task AnUnlistableSlotFolder_IsSaid_AndTheSessionsFoundAreKept()
    {
        var vs = new ConversationStore(_dir, ConversationStore.VisualStudioSlot);
        await vs.SaveAsync("feature-x", [new("user", "invoice totals, archived")], CancellationToken.None, workspaceRoot: null);
        Directory.CreateDirectory(SlotDir);
        var info = new DirectoryInfo(SlotDir);
        var acl  = info.GetAccessControl();
        _deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        acl.AddAccessRule(_deny);
        info.SetAccessControl(acl);

        var scan = await vs.SearchAsync("invoice totals", CancellationToken.None);

        Assert.Equal("feature-x", Assert.Single(scan.Items).Name);
        Assert.Equal(SlotDir, Assert.Single(scan.Unreadable));
    }
}
#endif
