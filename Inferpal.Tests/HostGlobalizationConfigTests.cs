using System.IO;
using System.Xml.Linq;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Locks the libicu fallback of the VS Code extension (§23). On a bare Linux without libicu, the
/// self-contained host FailFasts at boot; the extension then respawns it with
/// <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1</c> (hostClient.ts). That fallback only works
/// because the published runtimeconfig carries <b>no</b> <c>System.Globalization.Invariant</c>
/// entry: the SDK emits one as soon as the csproj declares <c>InvariantGlobalization</c>
/// (either value), and the AppContext switch then overrides the environment variable — the retry
/// spawns a host that crashes identically. Measured on the WSL bench of 2026-08-17
/// (docs/probes/icu-fallback/).
/// </summary>
public class HostGlobalizationConfigTests
{
    [Fact]
    public void HostCsprojNeverDeclaresInvariantGlobalization()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        var csproj = Path.Combine(dir!, "Inferpal.Host", "Inferpal.Host.csproj");
        var declared = XDocument.Load(csproj)
            .Descendants()
            .Where(e => e.Name.LocalName == "InvariantGlobalization")
            .ToList();

        Assert.True(declared.Count == 0,
            "Inferpal.Host.csproj declares <InvariantGlobalization> — even 'false' makes the SDK " +
            "write the AppContext switch into the runtimeconfig, which silently disables the " +
            "extension's libicu fallback (DOTNET_SYSTEM_GLOBALIZATION_INVARIANT). Remove the " +
            "property: the SDK default is already non-invariant, without locking the env var.");
    }
}
