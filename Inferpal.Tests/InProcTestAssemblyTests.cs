#if WINDOWS
using System.Reflection;
using System.Runtime.Versioning;
using Inferpal.GhostText;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The in-process assembly these tests run is its net8.0-windows build, never the net472 one the
/// VSIX ships.
/// </summary>
/// <remarks>
/// Both land in this output folder under the same name: the direct reference brings the net8 build,
/// and the VSIX project's packaged content brings the net472 one transitively. Whichever copy ran last
/// won, depending on build order. The net472 build compiles its own copy of the signal bus, whose test
/// seams nothing here sets: FimSidecarLifecycleTests then failed at random (the reason was written by
/// the other copy), and in-process code under test wrote signal files into the real %TEMP%\Inferpal
/// that a running Visual Studio reads.
/// </remarks>
public class InProcTestAssemblyTests
{
    [Fact]
    public void TheInProcAssemblyUnderTest_IsItsNet8Build()
    {
        var assembly  = typeof(FimSidecar).Assembly;
        var framework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        Assert.True(framework?.StartsWith(".NETCoreApp", StringComparison.Ordinal) == true,
            $"{assembly.Location} targets {framework ?? "(unknown)"}: the net472 build shipped in the VSIX "
            + "overwrote the one these tests compile against.");
    }
}
#endif
