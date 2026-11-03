using System.IO;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a workspace root reads is read again when the person comes back to it (A → B → A): its C# compilation, its map,
/// and the <c>indexExclude</c> of its <c>.inferpal/project.json</c> as soon as it changes.
/// </summary>
public sealed class RootStateTests : IDisposable
{
    private readonly string _a = Directory.CreateTempSubdirectory("inferpal-root-a-").FullName;
    private readonly string _b = Directory.CreateTempSubdirectory("inferpal-root-b-").FullName;
    private readonly List<ProjectIndexService> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        CSharpSemanticIndex.Forget(_a);
        CSharpSemanticIndex.Forget(_b);
        foreach (var d in new[] { _a, _b }) try { Directory.Delete(d, recursive: true); } catch { }
    }

    private ProjectIndexService Service()
    {
        var svc = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig { RagEnabled = false }, new LspSemanticProvider());
        _services.Add(svc);
        return svc;
    }

    /// <summary>The file opened for this test alone — tried again while the live watcher still reads it.</summary>
    private static FileStream Exclusive(string path)
    {
        for (var tries = 0; ; tries++)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (tries < 100) { Thread.Sleep(20); }
        }
    }

    /// <summary>The files the index of <paramref name="root"/> holds, built if it is not yet — as its first use builds it.</summary>
    private static int FilesSeen(string root)
    {
        var index = CSharpSemanticIndex.ForWorkspace(root);
        if (index.FileCount == 0) index.Build();
        return index.FileCount;
    }

    /// <summary>Its watcher watches one root at a time: what changed in a root while another was open is in its
    /// compilation when the person comes back — a subfolder's index too (rename_symbol narrows its root).</summary>
    [Fact]
    public void BackOnARoot_ItsCSharpIndexHoldsWhatChangedWhileAway()
    {
        var src = Path.Combine(_a, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "Foo.cs"), "class Foo { }");
        var svc = Service();
        svc.SetRoot(_a);
        Assert.Equal(1, FilesSeen(_a));   // built at its first use
        Assert.Equal(1, FilesSeen(src));

        svc.SetRoot(_b);
        File.WriteAllText(Path.Combine(src, "Bar.cs"), "class Bar { }");   // a pull in A while B is open
        svc.SetRoot(_a);

        Assert.Equal(2, FilesSeen(_a));
        Assert.Equal(2, FilesSeen(src));
        Assert.Equal(2, svc.RootSwitches);
    }

    /// <summary>The root left is forgotten too: its compilation does not stay in memory for the rest of the session.</summary>
    [Fact]
    public void TheRootLeft_IsForgotten()
    {
        File.WriteAllText(Path.Combine(_a, "Foo.cs"), "class Foo { }");
        var svc = Service();
        svc.SetRoot(_a);
        Assert.Equal(1, CSharpSemanticIndex.ForWorkspace(_a).Let(i => { i.Build(); return i.FileCount; }));

        svc.SetRoot(_b);
        Assert.Equal(0, CSharpSemanticIndex.ForWorkspace(_a).FileCount);   // a new, unbuilt index: the old one is gone
    }

    /// <summary>Reference arm: pinning the same root again (every heartbeat) keeps everything.</summary>
    [Fact]
    public void TheSameRootPinnedAgain_KeepsItsIndex()
    {
        File.WriteAllText(Path.Combine(_a, "Foo.cs"), "class Foo { }");
        var svc = Service();
        svc.SetRoot(_a);
        Assert.Equal(1, FilesSeen(_a));
        File.WriteAllText(Path.Combine(_a, "Bar.cs"), "class Bar { }");
        svc.SetRoot(_a);

        Assert.Equal(0, svc.RootSwitches);
    }

    [Fact]
    public void AnEditedProfile_ChangesTheIndexExclusions_WithoutARestart()
    {
        var svc = Service();
        svc.SetRoot(_a);
        Assert.Empty(svc.ProfileExcludes);   // witness: nothing excluded yet

        var profile = Services.Governance.ProjectProfile.PathIn(_a);
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, """{ "indexExclude": ["generated/**"] }""");

        // The state, not the return: the live watcher may have taken the edit first — what the person needs.
        svc.OnProfileChanged(profile);
        Assert.Equal(["generated/**"], svc.ProfileExcludes);
        // Another project.json (a package's) changes nothing.
        Assert.False(svc.OnProfileChanged(Path.Combine(_a, "web", "project.json")));

        // Unreadable mid-save: the exclusions in force stay — never "none". (A lock that excludes readers is Windows's.)
        if (OperatingSystem.IsWindows())
        {
            using (Exclusive(profile))
                Assert.False(svc.OnProfileChanged(profile));
            Assert.Equal(["generated/**"], svc.ProfileExcludes);
        }
        // Truncated before it is written again (a save in two steps): mid-save too.
        var whole = File.ReadAllText(profile);
        File.WriteAllText(profile, "");
        Assert.False(svc.OnProfileChanged(profile));
        Assert.Equal(["generated/**"], svc.ProfileExcludes);
        File.WriteAllText(profile, whole);

        // Gone: no exclusion.
        File.Delete(profile);
        svc.OnProfileChanged(profile);
        Assert.Empty(svc.ProfileExcludes);
    }

    /// <summary>A file the profile excludes since it was indexed leaves the index, as one an ignore file excludes.</summary>
    [Fact]
    public void AFileTheProfileNowExcludes_IsExcludedFromTheIndex()
    {
        var svc = Service();
        svc.SetRoot(_a);
        var generated = Path.Combine(_a, "generated", "Big.cs");
        Assert.False(svc.IsExcluded(generated));   // witness

        var profile = Services.Governance.ProjectProfile.PathIn(_a);
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, """{ "indexExclude": ["generated/**"] }""");
        svc.OnProfileChanged(profile);

        Assert.True(svc.IsExcluded(generated));
        var rag = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(),
            "Inferpal.Core", "Services", "Rag", "ProjectIndexService.cs"));
        // Re-judged and evicted by both exclusions, never by the ignore files alone.
        Assert.Contains("var changed = indexed.Where(IsExcluded)", rag, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentExclusions.Of(file) is not null)", rag, StringComparison.Ordinal);
    }

    /// <summary>Typed by the person, <c>/map</c> maps the code as it is now.</summary>
    [Fact]
    public void ATypedMap_AsksForAFreshMap()
    {
        var route = SlashCommandRouter.Route("/map", []);
        var action = Assert.IsType<SlashToolAction>(route);
        Assert.Equal("generate_project_map", action.Tool);
        Assert.Contains("\"refresh\":true", System.Text.Json.JsonSerializer.Serialize(action.Args), StringComparison.Ordinal);
        // And a map made before a switch of root is not served after it, whatever its age.
        var map = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Core", "Services", "ProjectMapService.cs"));
        Assert.Contains("cached.Switches == switches", map, StringComparison.Ordinal);
    }
}

internal static class RootStateTestExtensions
{
    /// <summary>Applies <paramref name="f"/> to the value: an expression for one step of a test.</summary>
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> f) => f(value);
}
