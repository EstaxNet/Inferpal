using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Guards the project rule "every localization key is translated in all 10 .resx". A missing key
/// does not fail anywhere at runtime — the resource manager silently falls back to English — so
/// the drift is invisible until a user reports a half-translated UI. Eight keys had already
/// slipped through before this test existed.
/// </summary>
public class LocalizationCompletenessTests
{
    private static readonly string[] Locales = ["fr", "de", "es", "it", "ru", "ja", "ko", "pl", "zh-CN"];

    /// <summary>Walks up from the test binary to the repository root (where the .sln lives).</summary>
    private static string LocalizationDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, "Inferpal.Core", "Localization");
    }

    // ── VS Code front-end (vscode/l10n/bundle.l10n.<locale>.json) ─────────────────────────
    //
    // Same failure mode as the .resx above, one editor further: `vscode.l10n.t()` falls back to
    // the English source string when a bundle lacks the key, so a forgotten locale ships as a
    // half-translated UI that nothing reports. Nothing guarded these nine files at all — the
    // English side is not a file but the literals in vscode/src, so the check that stands on its
    // own is bundle-against-bundle: the nine must agree on their key set.

    private static string VsCodeL10nDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, "vscode", "l10n");
    }

    private static HashSet<string> BundleKeys(string path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement
            .EnumerateObject()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void EveryVsCodeBundle_CarriesTheSameKeys()
    {
        var dir     = VsCodeL10nDir();
        var bundles = Directory.GetFiles(dir, "bundle.l10n.*.json").Order().ToList();

        // The witness: a renamed folder or a broken glob would make every rule below pass by
        // scanning nothing — the exact way the eight false verdicts of August were built.
        Assert.Equal(Locales.Length, bundles.Count);
        var union = bundles.SelectMany(BundleKeys).ToHashSet(StringComparer.Ordinal);
        Assert.True(union.Count > 100, $"Only {union.Count} keys read across {bundles.Count} bundles — the check is scanning nothing useful.");

        var report = new List<string>();
        foreach (var bundle in bundles)
        {
            var missing = union.Except(BundleKeys(bundle)).Order().ToList();
            if (missing.Count > 0)
                report.Add($"{Path.GetFileName(bundle)}: {missing.Count} missing → {string.Join(", ", missing.Take(5))}");
        }

        Assert.True(report.Count == 0,
            "Untranslated VS Code keys (add them to the listed bundles):\n" + string.Join("\n", report));
    }

    private static HashSet<string> KeysOf(string resxPath) =>
        XDocument.Load(resxPath).Root!
            .Elements("data")
            .Select(e => e.Attribute("name")?.Value ?? string.Empty)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void EveryLocale_TranslatesEveryKeyOfTheNeutralResx()
    {
        var dir     = LocalizationDir();
        var neutral = KeysOf(Path.Combine(dir, "Strings.resx"));
        Assert.NotEmpty(neutral);

        var report = new List<string>();
        foreach (var locale in Locales)
        {
            var missing = neutral.Except(KeysOf(Path.Combine(dir, $"Strings.{locale}.resx"))).Order().ToList();
            if (missing.Count > 0)
                report.Add($"{locale}: {missing.Count} missing → {string.Join(", ", missing.Take(10))}");
        }

        Assert.True(report.Count == 0,
            "Untranslated keys (add them to the listed .resx files):\n" + string.Join("\n", report));
    }

    [Fact]
    public void NoLocale_CarriesAKeyTheNeutralResxDropped()
    {
        // The mirror check: a key deleted from the source but left in the translations is dead
        // weight that later reads as "already translated" when the name is reused.
        var dir     = LocalizationDir();
        var neutral = KeysOf(Path.Combine(dir, "Strings.resx"));

        var report = new List<string>();
        foreach (var locale in Locales)
        {
            var extra = KeysOf(Path.Combine(dir, $"Strings.{locale}.resx")).Except(neutral).Order().ToList();
            if (extra.Count > 0)
                report.Add($"{locale}: {extra.Count} orphaned → {string.Join(", ", extra.Take(10))}");
        }

        Assert.True(report.Count == 0,
            "Orphaned keys (remove them from the listed .resx files):\n" + string.Join("\n", report));
    }
}
