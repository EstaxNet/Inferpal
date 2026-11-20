using System.Net;
using System.Net.Sockets;
using Inferpal.Services.Shell;
using Xunit;
using Listener = Inferpal.Services.Shell.LocalPorts.Listener;
using ProcessEntry = Inferpal.Services.Shell.LocalPorts.ProcessEntry;

namespace Inferpal.Tests;

/// <summary>
/// <see cref="LocalPorts"/>: whether a port of this machine is served by an app the agent started — every socket on it, the
/// test's own process listening, read on each system the CI runs (<c>Get-NetTCPConnection</c>, <c>/proc</c>, <c>lsof</c>).
/// </summary>
public sealed class LocalPortsTests
{
    [Fact]
    public async Task APortThisProcessListensOn_IsServedByItsTree_AndByNoOtherOne()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var (listeners, processes) = await LocalPorts.SnapshotAsync(port, default);
            Assert.Contains(listeners, l => l.Pid == Environment.ProcessId);

            Assert.True(await LocalPorts.ServedByAsync(port, [Environment.ProcessId], default));
            Assert.False(await LocalPorts.ServedByAsync(port, [int.MaxValue - 7], default));   // reference arm, end to end
            // An ancestor of the listener (the app under the job's shell), judged on the snapshot already taken: each new
            // one is a system query, slow on a busy machine.
            if (processes.TryGetValue(Environment.ProcessId, out var self) && self.Parent > 0 && processes.ContainsKey(self.Parent))
                Assert.True(LocalPorts.Served(listeners, [self.Parent], processes));
            Assert.False(await LocalPorts.ServedByAsync(port, [], default));
        }
        finally { listener.Stop(); }
    }

    private static readonly DateTime T = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<int, ProcessEntry> Tree = new()
    {
        [1] = new(0, T),
        [5] = new(1, T.AddMinutes(1)),    // the job's shell
        [10] = new(5, T.AddMinutes(2)),   // the app it started
        [20] = new(1, T.AddMinutes(3)),   // the person's own server
    };

    [Fact]
    public void EverySocketOnThePort_MustBeTheAgentsApps()
    {
        Assert.True(LocalPorts.Served([new Listener(10)], [5], Tree));   // reference arm
        // The person's server beside the agent's on the same port ([::1] and 127.0.0.1): a browser may reach either.
        Assert.False(LocalPorts.Served([new Listener(10), new Listener(20)], [5], Tree));
        // A socket whose owner cannot be read: another account's.
        Assert.False(LocalPorts.Served([new Listener(10), new Listener(0)], [5], Tree));
        Assert.False(LocalPorts.Served([], [5], Tree));
    }

    [Fact]
    public void AParentStartedAfterItsChild_IsAnotherProcessThatGotADeadParentsId()
    {
        var reused = new Dictionary<int, ProcessEntry>
        {
            [1] = new(0, T),
            [10] = new(5, T.AddMinutes(1)),
            [5] = new(1, T.AddMinutes(2)),   // started after 10: not its parent
        };
        Assert.False(LocalPorts.Descends(10, [5], reused));
        Assert.True(LocalPorts.Descends(10, [5], Tree));   // reference arm
        // A parent the list does not hold is never stepped to.
        Assert.False(LocalPorts.Descends(10, [5], new Dictionary<int, ProcessEntry> { [10] = new(5, T) }));
    }

    [Fact]
    public void AProcessStartedAfterTheSocketItIsSaidToOwn_IsNotItsOwner()
    {
        Assert.False(LocalPorts.Served([new Listener(10, T.AddMinutes(1))], [5], Tree));   // the socket predates process 10
        Assert.True(LocalPorts.Served([new Listener(10, T.AddMinutes(5))], [5], Tree));    // reference arm
    }

    /// <summary>Windows dates a socket to the second, a process to the tick: a server listening within its first second
    /// (a socket at hh:mm:25, its process at hh:mm:25.84) owns its socket. A time given with its fraction keeps no slack.</summary>
    [Fact]
    public void ASocketDatedToTheSecond_BelongsToTheProcessThatStartedWithinIt()
    {
        var tree = new Dictionary<int, ProcessEntry>(Tree) { [10] = new(5, T.AddMinutes(2).AddMilliseconds(840)) };
        Assert.True(LocalPorts.Served([new Listener(10, T.AddMinutes(2))], [5], tree));
        Assert.False(LocalPorts.Served([new Listener(10, T.AddMinutes(2).AddSeconds(-1))], [5], tree));   // a second earlier: another's
        Assert.False(LocalPorts.Served([new Listener(10, T.AddMinutes(2).AddMilliseconds(100))], [5], tree));   // exact, and before it
    }
}
