using System;
using System.Collections.Generic;
using Inferpal.Services.VsIntegration;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The out-of-process host keys the signal channels on the devenv that owns it. Visual Studio 2022 starts the host
/// through the ServiceHub controller: keyed on the direct parent, the host read the controller's channels while the
/// in-process package wrote devenv's, and the solution — the workspace root — never reached the chat.
/// </summary>
public class OwningDevenvTests
{
    private static Func<int, (string Name, int ParentId)?> Tree(params (int Pid, string Name, int Parent)[] procs)
    {
        var map = new Dictionary<int, (string, int)>();
        foreach (var (pid, name, parent) in procs) map[pid] = (name, parent);
        return pid => map.TryGetValue(pid, out var p) ? p : null;
    }

    [Fact]
    public void TheHostOfVisualStudio2026_IsADirectChild()
    {
        var (pid, _) = ParentProcess.NearestDevenv(30,
            Tree((30, "ServiceHub.Host.Extensibility.x64", 10), (10, "devenv", 1), (1, "explorer", 0)));
        Assert.Equal(10, pid);
    }

    [Fact]
    public void TheHostOfVisualStudio2022_IsFoundThroughTheServiceHubController()
    {
        // Measured: ServiceHub.Host.Extensibility.x64 → Microsoft.ServiceHub.Controller → devenv.
        var (pid, seen) = ParentProcess.NearestDevenv(6564, Tree(
            (6564, "ServiceHub.Host.Extensibility.x64", 7408),
            (7408, "Microsoft.ServiceHub.Controller", 2208),
            (2208, "devenv", 900),
            (900, "explorer", 0)));
        Assert.Equal(2208, pid);
        Assert.Contains("Microsoft.ServiceHub.Controller", seen);
    }

    [Fact]
    public void NoDevenvWithinTheBound_IsNoKey_AndSaysWhatWasSeen()
    {
        // A devenv one generation past the bound (MaxAncestorDepth = 4) is not this host's.
        var (pid, seen) = ParentProcess.NearestDevenv(6,
            Tree((6, "dotnet", 5), (5, "a", 4), (4, "b", 3), (3, "c", 2), (2, "d", 1), (1, "devenv", 0)));
        Assert.Null(pid);
        Assert.Equal("a (pid 5) <- b (pid 4) <- c (pid 3) <- d (pid 2)", seen);
        // Reference arm: the same devenv inside the bound is found.
        Assert.Equal(1, ParentProcess.NearestDevenv(5,
            Tree((5, "a", 4), (4, "b", 3), (3, "c", 2), (2, "d", 1), (1, "devenv", 0))).Pid);
    }

    [Fact]
    public void ALoopOrAnUnreadableParent_EndsTheWalk_WithoutAKey()
    {
        Assert.Null(ParentProcess.NearestDevenv(7, Tree((7, "host", 7))).Pid);
        Assert.Null(ParentProcess.NearestDevenv(7, Tree((7, "host", 8), (8, "x", 7))).Pid);
        var (pid, seen) = ParentProcess.NearestDevenv(7, Tree((7, "host", 99)));
        Assert.Null(pid);
        Assert.Contains("unreadable", seen);
    }
}
