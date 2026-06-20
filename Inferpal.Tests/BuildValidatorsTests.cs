using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Pure validator resolution: extension → ecosystem selection, project-root walk-up, the
// .inferpal/validators.json overlay parsing, and overlay-overrides-default precedence.
public class BuildValidatorsTests
{
    // Fake marker lookup over an in-memory set of "dir contains filename" facts. Glob support:
    // "*.ext" matches by extension, anything else is an exact filename match.
    private static Func<string, string, string?> FakeFinder(Dictionary<string, string[]> filesByDir) =>
        (dir, glob) =>
        {
            if (!filesByDir.TryGetValue(dir, out var files)) return null;
            string? hit = glob.StartsWith("*.")
                ? files.FirstOrDefault(f => f.EndsWith(glob[1..], StringComparison.OrdinalIgnoreCase))
                : files.FirstOrDefault(f => string.Equals(f, glob, StringComparison.OrdinalIgnoreCase));
            return hit is null ? null : dir + "\\" + hit;
        };

    private static IReadOnlyList<BuildValidator> Defaults => BuildValidators.Defaults;

    // ── Selection + walk-up ───────────────────────────────────────────────────

    [Fact]
    public void Match_DotnetFile_FindsNearestCsproj()
    {
        var finder = FakeFinder(new() { [@"C:\repo\app"] = ["App.csproj"] });
        var m = BuildValidators.Match(@"C:\repo\app\src\Service.cs", Defaults, finder);

        Assert.NotNull(m);
        Assert.Equal("dotnet", m!.Value.Validator.Name);
        Assert.Equal(@"C:\repo\app", m.Value.ProjectDir);
        Assert.Equal(@"C:\repo\app\App.csproj", m.Value.ProjectFile);
    }

    [Fact]
    public void Match_PrefersCsprojOverSlnInSameDir()
    {
        var finder = FakeFinder(new() { [@"C:\repo"] = ["Sln.sln", "Proj.csproj"] });
        var m = BuildValidators.Match(@"C:\repo\Foo.cs", Defaults, finder);
        Assert.Equal(@"C:\repo\Proj.csproj", m!.Value.ProjectFile);   // *.csproj marker comes first
    }

    [Fact]
    public void Match_FallsBackToSlnWhenNoCsproj()
    {
        var finder = FakeFinder(new() { [@"C:\repo"] = ["Only.sln"] });
        var m = BuildValidators.Match(@"C:\repo\src\Foo.cs", Defaults, finder);
        Assert.Equal(@"C:\repo\Only.sln", m!.Value.ProjectFile);
    }

    [Fact]
    public void Match_TypeScriptFile_FindsTsconfig()
    {
        var finder = FakeFinder(new() { [@"C:\web"] = ["tsconfig.json"] });
        var m = BuildValidators.Match(@"C:\web\src\a\b\comp.tsx", Defaults, finder);
        Assert.Equal("typescript", m!.Value.Validator.Name);
        Assert.Equal(@"C:\web\tsconfig.json", m.Value.ProjectFile);
    }

    [Fact]
    public void Match_RustAndGo_ByMarker()
    {
        var rust = BuildValidators.Match(@"C:\r\src\main.rs", Defaults,
            FakeFinder(new() { [@"C:\r"] = ["Cargo.toml"] }));
        Assert.Equal("rust", rust!.Value.Validator.Name);

        var go = BuildValidators.Match(@"C:\g\pkg\main.go", Defaults,
            FakeFinder(new() { [@"C:\g"] = ["go.mod"] }));
        Assert.Equal("go", go!.Value.Validator.Name);
    }

    [Fact]
    public void Match_UnknownExtension_ReturnsNull()
    {
        var finder = FakeFinder(new() { [@"C:\x"] = ["whatever.txt"] });
        Assert.Null(BuildValidators.Match(@"C:\x\notes.md", Defaults, finder));
    }

    [Fact]
    public void Match_NoMarkerAnywhere_ReturnsNull()
    {
        var finder = FakeFinder(new());   // nothing on disk
        Assert.Null(BuildValidators.Match(@"C:\repo\src\Service.cs", Defaults, finder));
    }

    // ── Overlay parsing ────────────────────────────────────────────────────────

    [Fact]
    public void ParseConfig_ReadsExtensionsMarkerAndCommand()
    {
        var v = BuildValidators.ParseConfig(
            """{ ".ts,.tsx": { "marker": "tsconfig.json", "command": "npx tsc --noEmit" } }""");
        Assert.Single(v);
        Assert.Equal([".ts", ".tsx"], v[0].Extensions);
        Assert.Equal(["tsconfig.json"], v[0].Markers);
        Assert.Equal("npx tsc --noEmit", v[0].Command);
        Assert.False(v[0].UseDotnetErrorFilter);
    }

    [Fact]
    public void ParseConfig_AcceptsMarkerArray_AndNormalizesExtensions()
    {
        var v = BuildValidators.ParseConfig(
            """{ "py": { "marker": ["pyproject.toml", "setup.py"], "command": "ruff check ." } }""");
        Assert.Equal([".py"], v[0].Extensions);             // leading dot added
        Assert.Equal(["pyproject.toml", "setup.py"], v[0].Markers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ ".ts": { "command": "tsc" } }""")]            // no marker
    [InlineData("""{ ".ts": { "marker": "tsconfig.json" } }""")]   // no command
    public void ParseConfig_InvalidOrIncomplete_ReturnsEmpty(string json)
    {
        Assert.Empty(BuildValidators.ParseConfig(json));
    }

    // ── Overlay precedence ──────────────────────────────────────────────────────

    [Fact]
    public void Resolve_OverlayOverridesDefaultForSameExtension()
    {
        var overlay = BuildValidators.ParseConfig(
            """{ ".cs": { "marker": "*.csproj", "command": "custom-build" } }""");
        var validators = BuildValidators.Resolve(overlay);

        var finder = FakeFinder(new() { [@"C:\repo"] = ["App.csproj"] });
        var m = BuildValidators.Match(@"C:\repo\Foo.cs", validators, finder);

        Assert.Equal("custom-build", m!.Value.Validator.Command);     // overlay wins
        Assert.False(m.Value.Validator.UseDotnetErrorFilter);         // overlay entry is generic
    }
}
