using System.Diagnostics;
using System.IO;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A local path whose links lead to another machine is refused from the links' own data, before anything follows them —
/// a chain of local links included; a workspace that is itself a link to a share keeps its files.
/// </summary>
/// <remarks>⚠ Following a link IS the connection: <c>read_file net/a.txt</c>, <c>net</c> a folder link under the workspace
/// to <c>\\host\share</c>, opened an SMB connection and waited 21 s before refusing the path as outside the workspace
/// (measured, <c>V109aSmbProbeTests.ALocalLinkToTheNetwork_CountsOutboundSmb</c>). The hosts are TEST-NET-1: nothing answers
/// them, so a test that follows one takes 21 s and fails on its clock. Windows only; a system that refuses to make the links
/// returns early (this suite has no skip).</remarks>
public sealed class NetworkLinkPathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-netlink-").FullName;
    private readonly List<string> _links = [];

    public void Dispose()
    {
        foreach (var link in _links) try { Directory.Delete(link); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private bool Link(string at, string target)
    {
        try { Directory.CreateSymbolicLink(at, target); _links.Insert(0, at); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    [Fact]
    public void ALinkUnderTheWorkspaceToAShare_IsRefusedAtOnce_NamingWhereItLeads()
    {
        if (!OperatingSystem.IsWindows() || !Link(Path.Combine(_root, "net"), @"\\192.0.2.250\share")) return;
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var file = Path.Combine(_root, "net", "a.txt");

        var clock = Stopwatch.StartNew();
        Assert.Equal(@"\\192.0.2.250\share\a.txt", NetworkPath.Landing(file));
        var refused = Assert.Throws<ArgumentException>(() => PathSanitizer.AssertUnderRoot(file, _root));
        Assert.Contains("a link on this path leads to another machine", refused.Message);
        Assert.Contains(@"\\192.0.2.250\share\a.txt", refused.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed}: the link was followed to the host");

        // Reference arm: an ordinary folder of the workspace leads nowhere.
        Assert.Null(NetworkPath.Landing(Path.Combine(_root, "src", "a.txt")));
        PathSanitizer.AssertUnderRoot(Path.Combine(_root, "src", "a.txt"), _root);
    }

    /// <summary>A local link to a link to a share: reading the next component through the first link was the connection.</summary>
    [Fact]
    public void AChainOfLocalLinksToAShare_IsWalkedAsText_AndRefusedAtOnce()
    {
        if (!OperatingSystem.IsWindows() || !Link(Path.Combine(_root, "net"), @"\\192.0.2.251\share")
            || !Link(Path.Combine(_root, "hop"), "net")) return;   // relative: resolved against the link's folder

        var clock = Stopwatch.StartNew();
        Assert.Equal(@"\\192.0.2.251\share\deep\a.txt", NetworkPath.Landing(Path.Combine(_root, "hop", "deep", "a.txt")));
        Assert.True(NetworkPath.LeadsOutside(Path.Combine(_root, "hop", "deep", "a.txt"), _root));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed}: a link of the chain was followed to the host");
    }

    /// <summary>A workspace that is itself a link to a share: its own files land inside it, never "on another machine".</summary>
    [Fact]
    public void AWorkspaceThatIsALinkToAShare_KeepsItsFiles()
    {
        var workspace = Path.Combine(_root, "ws");
        if (!OperatingSystem.IsWindows() || !Link(workspace, @"\\192.0.2.252\src")) return;

        var clock = Stopwatch.StartNew();
        Assert.False(NetworkPath.LeadsOutside(Path.Combine(workspace, "a.cs"), workspace));
        Assert.True(NetworkPath.LeadsOutside(Path.Combine(workspace, "..", "elsewhere.cs"), workspace) is false);   // local, outside: not this rule's
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed}: a link was followed to the host");
    }
}
