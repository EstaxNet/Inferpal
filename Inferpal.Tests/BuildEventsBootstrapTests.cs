using System.Reflection;
using Inferpal.GhostText;
using Microsoft.VisualStudio.Shell.Interop;
using Xunit;

namespace Inferpal.Tests;

// §27.4 (pre-1.6.0 architecture review): two independent bootstraps — GhostTextPackage (registry-loaded) and
// GhostTextViewListener (MEF, first editor) — used to each create their own VsBuildEventHandler:
// every failed build was collected twice on the UI thread and the signal file written twice.
// Neither bootstrap can be removed (each is the only one that runs in SOME install world), so
// they funnel through BuildEventsBootstrap: whoever arrives first creates the ONE handler.
public class BuildEventsBootstrapTests
{
    /// <summary>Counting COM stand-in: DispatchProxy implements the whole interop interface,
    /// we only care that AdviseUpdateSolutionEvents is called exactly once.</summary>
    public class CountingComProxy : DispatchProxy
    {
        public int Advises;
        public int Unadvises;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "AdviseUpdateSolutionEvents":
                    Advises++;
                    if (args is { Length: 2 }) args[1] = 1u;   // out cookie
                    return 0;                                   // S_OK
                case "UnadviseUpdateSolutionEvents":
                    Unadvises++;
                    return 0;
                default:
                    return 0;                                   // any other HRESULT-shaped call
            }
        }
    }

    [Fact]
    public void BothBootstraps_ProduceExactlyOneSubscription()
    {
        var buildMgr = DispatchProxy.Create<IVsSolutionBuildManager2, CountingComProxy>();
        var solution = DispatchProxy.Create<IVsSolution, CountingComProxy>();
        var counter  = (CountingComProxy)(object)buildMgr;

        try
        {
            // The two real-world entry points, in either order:
            BuildEventsBootstrap.EnsureCreated(buildMgr, solution, taskList: null);   // package
            BuildEventsBootstrap.EnsureCreated(buildMgr, solution, taskList: null);   // MEF listener

            Assert.True(BuildEventsBootstrap.IsCreated);
            Assert.Equal(1, counter.Advises);   // ONE handler, ONE Advise — the §27.4 invariant
        }
        finally
        {
            BuildEventsBootstrap.DisposeHandler();
        }

        Assert.False(BuildEventsBootstrap.IsCreated);
        Assert.Equal(1, counter.Unadvises);     // shutdown unadvises the one subscription
    }
}
