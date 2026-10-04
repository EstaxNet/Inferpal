using System.IO;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Inferpal.Config;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A settings hint that states a default states THE default. The context-window hint said "4096 (default)" while the
/// setting defaults to 8192 — on the field the user tunes to fit a model in memory — and, beside it, that the setting
/// should "match your model's num_ctx", when on Ollama it is what SETS num_ctx.
/// </summary>
public class HintDefaultsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static Dictionary<string, string> EnglishResources() =>
        XDocument.Load(Path.Combine(RepoRoot(), "Inferpal.Core", "Localization", "Strings.resx"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

    /// <summary>The default value of the config property a schema key names (its JSON name).</summary>
    private static object? DefaultOf(string key)
    {
        var property = typeof(InferpalConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name == key);
        return property?.GetValue(new InferpalConfig());
    }

    // Both ways a hint states one: "Default: 20" and "4096 (default)" — the defect wore the second.
    private static readonly Regex StatedDefault = new(@"\bDefault:\s*(\d+)|\b(\d+)\s*\(default\)", RegexOptions.CultureInvariant);

    [Fact]
    public void EveryNumericHintThatStatesADefault_StatesTheRealOne()
    {
        var resources = EnglishResources();
        var readHints = 0;
        var wrong = new List<string>();

        foreach (var field in SettingsSchema.AllFields.Where(f => f.Kind == SettingKind.Int && f.Hint is not null))
        {
            if (!resources.TryGetValue(field.Hint!, out var hint)) continue;
            readHints++;
            var stated = StatedDefault.Matches(hint)
                .Select(m => int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)).ToList();
            if (stated.Count == 0) continue;

            var actual = Convert.ToInt32(DefaultOf(field.Key));
            if (stated.Any(n => n != actual))
                wrong.Add($"{field.Hint} states {string.Join(", ", stated)}, {field.Key} defaults to {actual}");
        }

        // WITNESSES: the hints are read, and both forms are recognized. The current hints state no default
        // (the box shows the value in force), so the rule holds for the next hint that does.
        Assert.True(readHints >= 10, $"only {readHints} numeric hint(s) read: the reading is broken");
        Assert.Equal(2, StatedDefault.Matches("Default: 20, or 4096 (default)").Count);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void TheContextWindowHint_DoesNotSendTheUserToMatchAValueTheSettingSets()
    {
        var hint = EnglishResources()["HintContextWindowSize"];

        Assert.DoesNotContain("4096 (default)", hint);
        Assert.DoesNotContain("Match your model's num_ctx", hint);
        Assert.Contains("summarized", hint);           // the default handling — trimming is the fallback
    }
}
