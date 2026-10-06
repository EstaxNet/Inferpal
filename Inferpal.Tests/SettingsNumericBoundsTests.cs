using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A numeric settings box is read by TWO panels, and each held its own rules: the Visual Studio window clamped in
/// silence (50 results per search saved as 20, the box still showing 50 and "1 unsaved change" for ever) and restored a
/// cleared context window to 0, the VS Code panel applied no bound at all (50 saved) and restored 8192. Bounds now live
/// in the Core schema and factory values in <see cref="InferpalConfig"/>, and both panels read them: a value outside
/// the bounds is named and the saved value kept, a cleared box takes the factory value.
/// </summary>
public class SettingsNumericBoundsTests
{
    [Theory]
    [InlineData("ragTopK",                  1,  20)]
    [InlineData("kvCacheAnchorMessages",    0,  20)]
    [InlineData("compactionTimeoutSeconds", 10, 300)]
    [InlineData("contextWindowKeepTurns",   1,  null)]
    [InlineData("modelIdleTimeoutMinutes",  1,  null)]
    [InlineData("contextWindowSize",        0,  null)]
    [InlineData("agentMaxIterations",       0,  null)]
    [InlineData("oodaTurnThreshold",        0,  null)]
    [InlineData("commandTimeoutSeconds",    1,  null)]
    [InlineData("quickTimeoutSeconds",      10, null)]
    [InlineData("normalTimeoutSeconds",     10, null)]
    [InlineData("ragSimilarityThreshold",   0,  1)]
    public void TheSchema_DeclaresTheBounds_BothPanelsApply(string key, int min, int? max)
    {
        var field = SettingsSchema.Field(key);
        Assert.Equal(min, field.Min);
        Assert.Equal(max, field.Max);
        Assert.True(field.Accepts(min));
        Assert.False(field.Accepts(min - 1));
        if (max is { } top)
        {
            Assert.True(field.Accepts(top));
            Assert.False(field.Accepts(top + 1));
        }
    }

    [Fact]
    public void TheHost_ServesTheBounds_ToTheVsCodePanel()
    {
        var server = new HostServer(_ => new FakeInferenceProvider(), () => new InferpalConfig());
        var topK = server.SettingsSchemaModel().Tabs
            .SelectMany(t => t.Sections).SelectMany(s => s.Fields).Single(f => f.Key == "ragTopK");

        Assert.Equal(1,  topK.Min);
        Assert.Equal(20, topK.Max);
    }

    [Fact]
    public void TheVsCodePanel_RefusesAValueOutsideTheBounds_AndNamesIt()
    {
        var save = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("webview/settings.ts"), "function onSave(");
        var intCase = save[save.IndexOf("case 'int':", StringComparison.Ordinal)..save.IndexOf("case 'float':", StringComparison.Ordinal)];

        Assert.Contains("field.min", intCase, StringComparison.Ordinal);
        Assert.Contains("field.max", intCase, StringComparison.Ordinal);
        // The value is stored only under the bounds test; anything else falls through to "named".
        Assert.Matches(@"if \(ok && \(field\.min == null[^)]*\) && \(field\.max == null[^)]*\)\) \{\s*config\[field\.key\] = value;", intCase);
        Assert.Contains("ignored.push(", intCase, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Visual Studio window reads the same two sources: no bound of its own, no cleared value of its own. Each
    /// numeric <c>ReadInt</c> names a schema key and takes the factory value through <see cref="InferpalConfig"/>.
    /// </summary>
    [Fact]
    public void TheVisualStudioWindow_ReadsTheSchemasBounds_AndTheFactoryValues()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalSettingsData.cs");
        var code = ConventionCoverageTests.CodeOnly(path);

        var calls = Regex.Matches(code, @"=\s*ReadInt\((?<args>[^;]*)\);").Select(m => m.Groups["args"].Value).ToList();
        Assert.True(calls.Count >= 6, $"only {calls.Count} ReadInt call(s) found: the rule reads nothing");   // WITNESS

        foreach (var args in calls)
        {
            var key = Regex.Match(args, "\"(?<key>[a-zA-Z]+)\"").Groups["key"].Value;
            Assert.True(SettingsSchema.AllFields.Any(f => f.Key == key), $"ReadInt({args}) names no schema field");
            Assert.Matches(@"c\s*=>\s*c\.\w+\s*$", args.Trim());               // the factory value, read from the config
            Assert.DoesNotContain("Math.", args, StringComparison.Ordinal);   // no clamp of its own
        }
        Assert.DoesNotContain("Math.Clamp(compactTimeoutSec", code, StringComparison.Ordinal);
    }
}
