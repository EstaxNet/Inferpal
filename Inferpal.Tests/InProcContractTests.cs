using System.IO;
using System.Linq;
using System.Reflection;
using Inferpal.Config;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The contact points between the in-process half (net472, loaded by devenv) and the Core (net8,
/// out of its reach). Each is a place where two worlds talk to each other <b>with no compiler able
/// to keep them in agreement</b>: a rename on one side produces no error there, only a feature that
/// stops existing without a message. Same failure mode as the VSIX assets, guarded the same way.
/// </summary>
public class InProcContractTests
{
    // ── 1. The three settings the in-process half reads itself from config.json ────
    // InProcConfig cannot load InferpalConfig (net8): it re-reads the file and looks up three
    // properties by name. A rename on the Core side would disable ghost text silently - the reader
    // would find nothing and fall back to its defaults.

    [Theory]
    [InlineData("InlineCompletionEnabled", typeof(bool))]
    [InlineData("InlineCompletionMode",    typeof(string))]
    [InlineData("InlineCompletionModel",   typeof(string))]
    public void InProcConfig_ReadsPropertiesThatStillExistOnTheRealConfig(string name, Type expected)
    {
        var property = typeof(InferpalConfig).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

        Assert.True(property is not null,
            $"InferpalConfig.{name} no longer exists. Inferpal.InProc reads that name directly " +
            "from config.json (it cannot load the Core): without it, ghost text falls back to its " +
            "defaults instead of following the setting - with no message at all.");
        Assert.Equal(expected, property!.PropertyType);
    }

    [Fact]
    public void InProcConfig_UsesTheSameDefaultsAsTheRealConfig()
    {
        // The in-process reader applies its own defaults when the key is missing from the file
        // (the normal case: InferpalConfig only writes what has been touched).
        var reference = new InferpalConfig();

        Assert.True(reference.InlineCompletionEnabled);        // InProcConfig.Snapshot.Enabled
        Assert.Equal("Default", reference.InlineCompletionMode); // InProcConfig.Snapshot.Mode
        Assert.Equal(string.Empty, reference.InlineCompletionModel);
    }

    // ── 2. The operation names of the debugger bus ────────────────────────────────
    // The in-process driver and the out-of-process caller live in two assemblies AND two runtimes.
    // The literals live in DebugOps, compiled on both sides from the same file; this test checks
    // that the historical alias has not drifted.

    [Fact]
    public void DebugOps_AreTheOnlySourceOfTheWireNames()
    {
        var ops = typeof(Inferpal.Services.Debugging.DebugOps)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        Assert.NotEmpty(ops);
        Assert.Equal(ops.Count, ops.Values.Distinct().Count());   // aucun doublon sur le fil

        var session = typeof(Inferpal.Services.Debugging.SignalDebugSession)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith("Op"))
            .ToDictionary(f => f.Name.Substring(2), f => (string)f.GetRawConstantValue()!);

        Assert.Equal(ops.Count, session.Count);
        foreach (var pair in session)
            Assert.Equal(ops[pair.Key], pair.Value);
    }

    // ── 3. The inference sidecar ──────────────────────────────────────────────────
    // FimSidecar starts an executable by name. If it is not packaged, ghost text goes quiet.

    [Fact]
    public void Vsix_ShipsTheInProcAssemblyAndItsFimSidecar()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal", "Inferpal.csproj"));

        foreach (var file in new[] { "Inferpal.InProc.dll", "Inferpal.Fim.exe",
                                     "Inferpal.Fim.dll", "Inferpal.Fim.runtimeconfig.json" })
            Assert.True(csproj.Contains($"<Link>{file}</Link>"),
                $"{file} is no longer embedded in the VSIX by Inferpal.csproj. " +
                "The VSIX would still install, and the in-process half would be mute.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
