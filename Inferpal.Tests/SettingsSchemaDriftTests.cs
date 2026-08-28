using System.Xml.Linq;
using System.IO;
using Inferpal.Config;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The settings form is now declared once, in <see cref="SettingsSchema"/>, and served to the VS
/// Code panel over <c>settings/schema</c>. These tests are what makes that single source of truth
/// trustworthy: every declared field must be a real config key, and every label it points at must
/// exist in the resources.
/// </summary>
/// <remarks>
/// The drift this closes is not hypothetical: eight settings-window strings shipped untranslated
/// because they were added on the Visual Studio side and never propagated to the other front-end's
/// hand-written copy of the same form.
/// </remarks>
public class SettingsSchemaDriftTests
{
    /// <summary>Labels the adapter resolves itself rather than from the .resx.</summary>
    private static readonly string[] LocalLabels =
        [SettingsSchema.LocalLabelInlineDiff, SettingsSchema.LocalLabelTabTools];

    private static HashSet<string> ResourceNames()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        var resx = Path.Combine(dir!, "Inferpal.Core", "Localization", "Strings.resx");
        return XDocument.Load(resx).Root!
            .Elements("data")
            .Select(e => e.Attribute("name")?.Value ?? string.Empty)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void TheSchemaCoversTheWholeForm()
    {
        // Guards the guard: an empty or gutted schema must not make the checks below vacuous.
        Assert.Equal(4, SettingsSchema.Tabs.Count);
        Assert.InRange(SettingsSchema.AllFields.Count(), 30, 200);
    }

    [Fact]
    public void EveryFieldMapsToAnInferpalConfigProperty()
    {
        var properties = typeof(InferpalConfig).GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = SettingsSchema.AllFields
            .Select(f => f.Key)
            .Where(k => !properties.Contains(k))
            .Order()
            .ToList();

        Assert.True(unknown.Count == 0,
            "Settings fields with no matching InferpalConfig property (typo, or the property was "
            + "renamed/removed without updating the schema):\n  " + string.Join("\n  ", unknown));
    }

    [Fact]
    public void EveryFieldKeyAppearsOnlyOnce()
    {
        var duplicates = SettingsSchema.AllFields
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Order()
            .ToList();

        Assert.True(duplicates.Count == 0,
            "The same setting is editable twice (the two inputs would fight over the value):\n  "
            + string.Join("\n  ", duplicates));
    }

    [Fact]
    public void EveryLabelAndHintExistsInTheResources()
    {
        var resources = ResourceNames();

        var missing = SettingsSchema.AllFields
            .SelectMany(f => new[] { f.Label, f.Hint })
            .Concat(SettingsSchema.Tabs.Select(t => t.Title))
            .Concat(SettingsSchema.Tabs.SelectMany(t => t.Sections).SelectMany(sec =>
                new[] { sec.Title, sec.ToggleLabel, sec.ToggleHint }))
            .Where(n => !string.IsNullOrEmpty(n))
            .Where(n => !LocalLabels.Contains(n!))
            .Where(n => !resources.Contains(n!))
            .Distinct()
            .Order()
            .ToList();

        Assert.True(missing.Count == 0,
            "Settings labels/hints/titles with no matching resource (the panel would render the raw "
            + "key, and the .resx completeness test would never see them):\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void FimModeOptionTexts_MatchTheActualPresets()
    {
        // §27.6 - the labels promise "128 tok / 300 ms" literally: if the preset table of
        // FimContextBuilder moves, the form lies silently on both sides.
        var field = SettingsSchema.AllFields.Single(f => f.Key == "inlineCompletionMode");
        Assert.NotNull(field.Options);

        foreach (var opt in field.Options!)
        {
            var preset = FimContextBuilder.GetSettings(opt.Value);
            var delay  = preset.DebounceMs >= 1000
                ? $"{preset.DebounceMs / 1000} s"
                : $"{preset.DebounceMs} ms";
            Assert.Contains($"{preset.MaxTokens} tok", opt.Text);
            Assert.Contains(delay, opt.Text);
        }

        // GetSettings falls back to Default on an unknown code: pairwise-distinct presets prove
        // every option of the form is a real preset, not the fallback.
        var presets = field.Options!.Select(o => FimContextBuilder.GetSettings(o.Value)).ToList();
        Assert.Equal(presets.Count, presets.Distinct().Count());
    }

    [Fact]
    public void SelectFieldsDeclareTheirOptions()
    {
        var empty = SettingsSchema.AllFields
            .Where(f => f.Kind == SettingKind.Select)
            .Where(f => f.Options is null || f.Options.Count == 0)
            .Select(f => f.Key)
            .ToList();

        Assert.True(empty.Count == 0, "Select fields with no options:\n  " + string.Join("\n  ", empty));
    }
}
