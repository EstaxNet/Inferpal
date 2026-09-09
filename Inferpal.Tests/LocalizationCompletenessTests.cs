using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Guards the project rule "every localization key is translated in all 10 .resx". A missing key
/// does not fail anywhere at runtime — the resource manager silently falls back to English — so
/// the drift is invisible until a user reports a half-translated UI. Eight keys had already
/// slipped through before this test existed.
/// </summary>
/// <remarks>
/// The product shows translated text through FOUR channels, and each has its own way of staying
/// quiet when a key is missing: the <c>.resx</c> files (English fallback), the VS Code bundles
/// (<c>l10n.t</c> returns the source string), and the <b>VSIX command table</b> (an unresolved
/// <c>%Key%</c> token). All three are held here, each in both directions - the displayed string no
/// file translates, and the translation nothing displays any more.
/// </remarks>
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
        Assert.True(union.Count > 50, $"Only {union.Count} keys read across {bundles.Count} bundles — the check is scanning nothing useful.");

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

    /// <summary>Repository root - first ancestor of the test binary holding the .sln.</summary>
    private static string RepoRootDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    // ── The .resx files and Strings.cs: the half of the rule nobody held ───────────────────
    //
    // "Every new localization key -> translated in the 10 .resx AND A PROPERTY IN Strings.cs": the
    // two tests above hold the first half, the second was held by nobody. It fails silently on both
    // sides - Strings.Get returns the KEY itself when the resource is missing (`?? key`), so the
    // user reads "AgentPlanLabel" where a sentence belongs; and an entry no property asks for any
    // more is a dead translation nine languages keep carrying. Measured: 731 = 731, no gap. Free to
    // lock, therefore locked now rather than the day a violation appears.

    /// <summary>
    /// The keys <c>Strings.cs</c> actually asks for - <c>Get(nameof(X))</c> and the rare
    /// <c>Get("X")</c>. CARRIES THE WITNESS of its own enumeration.
    /// </summary>
    /// <remarks>
    /// Comments are neutralized (<c>ConventionCoverageTests.CodeOnly</c>): a commented-out property
    /// exposes nothing, and counting its key would make the mirror falsely green - the exact false
    /// green the neutralizer exists to close.
    /// </remarks>
    private static HashSet<string> StringsAccessorKeys()
    {
        var path = Path.Combine(LocalizationDir(), "Strings.cs");
        Assert.True(File.Exists(path), $"{path} does not exist - the rule checks nothing any more.");

        var code = ConventionCoverageTests.CodeOnly(path);
        var keys = Regex.Matches(code, @"(?<![A-Za-z0-9_])Get\(\s*nameof\(\s*([A-Za-z0-9_]+)\s*\)")
            .Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(code, @"(?<![A-Za-z0-9_])Get\(\s*""([^""]+)""")
                .Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(keys.Count > 100, $"Only {keys.Count} key(s) read from Strings.cs - the enumerator is too narrow.");
        Assert.Contains("LabelLanguage", keys);
        return keys;
    }

    [Fact]
    public void EveryNeutralResxKey_IsExposedByStrings()
    {
        var neutral = KeysOf(Path.Combine(LocalizationDir(), "Strings.resx"));
        Assert.NotEmpty(neutral);

        var orphaned = neutral.Except(StringsAccessorKeys()).Order().ToList();
        Assert.True(orphaned.Count == 0,
            $"{orphaned.Count} key(s) translated in all ten .resx that Strings.cs exposes to nobody "
            + "- either the property is missing, or the translation is dead: "
            + string.Join(", ", orphaned.Take(10)));
    }

    [Fact]
    public void NoStringsAccessor_AsksForAKeyTheNeutralResxLacks()
    {
        var neutral = KeysOf(Path.Combine(LocalizationDir(), "Strings.resx"));
        var missing = StringsAccessorKeys().Except(neutral).Order().ToList();

        Assert.True(missing.Count == 0,
            $"{missing.Count} propertie(s) of Strings.cs ask for a key the neutral .resx lacks. "
            + "Strings.Get then returns the KEY itself: the user reads the identifier instead of "
            + "the sentence, in all ten languages, and no build fails - "
            + string.Join(", ", missing.Take(10)));
    }

    // ── Third channel: the VSIX command table ─────────────────────────────────────────────
    //
    // The labels Visual Studio shows for OUR COMMANDS come neither from the .resx files nor from
    // the VS Code bundles: they come from Inferpal\Localization\string-resources.json (plus one
    // file per language), named from the code by a "%Key%" token in a CommandConfiguration or a
    // MenuConfiguration. The token ships AS IS inside the packaged extension.json - VS resolves it
    // at run time - so a token with no entry fails no build, neither Debug nor Release
    // warnings-as-errors.
    //
    // Measured: "%InferpalMapCommand%" - the Alt+M command, the one that opens the project
    // architecture map - had an entry in NONE of the ten files. The command works, its shortcut is
    // wired; but everywhere VS NAMES it (Tools > Options > Environment > Keyboard, feature search)
    // it had no name. Nine commands out of ten carried theirs: the tenth was the one nothing
    // looked at.

    /// <summary>
    /// The <c>%Key%</c> tokens the VS adapter references, token to declaring file. CARRIES THE
    /// WITNESS of its enumeration.
    /// </summary>
    /// <remarks>
    /// The pattern requires the WHOLE literal (<c>"%Key%"</c>, quotes included): that is the shape
    /// the SDK recognises, and the only one that tells a token from a format <c>%</c>. Comments are
    /// neutralized - a token quoted in prose is not a command.
    /// </remarks>
    private static Dictionary<string, string> CommandTokens()
    {
        var token = new Regex("\"%([A-Za-z0-9_.]+)%\"");
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in ConventionCoverageTests.ProjectSources("Inferpal"))
            foreach (Match m in token.Matches(ConventionCoverageTests.CodeOnly(file)))
                found.TryAdd(m.Groups[1].Value, Path.GetRelativePath(RepoRootDir(), file));

        // One per shape of declaration site: a command, then a menu.
        foreach (var witness in new[] { "InferpalChatCommand", "InferpalMenu" })
            Assert.True(found.ContainsKey(witness), $"The enumerator no longer sees \"%{witness}%\".");
        return found;
    }

    /// <summary>The neutral file, then the nine languages - "" means the neutral one.</summary>
    private static HashSet<string> CommandResourceKeys(string locale)
    {
        var dir  = Path.Combine(RepoRootDir(), "Inferpal", "Localization");
        var path = locale.Length == 0
            ? Path.Combine(dir, "string-resources.json")
            : Path.Combine(dir, locale, "string-resources.json");
        Assert.True(File.Exists(path), $"{path} does not exist - the rule checks nothing any more.");

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement
            .EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void EveryCommandToken_IsDeclaredInAllStringResources()
    {
        var tokens  = CommandTokens();
        var locales = new[] { string.Empty }.Concat(Locales).ToList();

        var gaps = tokens.Keys.Order()
            .Select(t => (Token: t, Missing: locales
                .Where(l => !CommandResourceKeys(l).Contains(t))
                .Select(l => l.Length == 0 ? "neutral" : l).ToList()))
            .Where(x => x.Missing.Count > 0)
            .Select(x => $"\"%{x.Token}%\" ({tokens[x.Token]}) missing in: {string.Join(", ", x.Missing)}")
            .ToList();

        Assert.True(gaps.Count == 0,
            "Command token with no label: VS has nothing to show wherever it names the command, "
            + "and no build says so -" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", gaps));
    }

    [Fact]
    public void NoStringResource_CarriesATokenNothingReferences()
    {
        // The mirror, for the same reason as NoLocale_CarriesAKeyTheNeutralResxDropped: a label
        // whose command is gone later reads as "already translated".
        var used   = CommandTokens().Keys.ToHashSet(StringComparer.Ordinal);
        var report = new List<string>();

        foreach (var locale in new[] { string.Empty }.Concat(Locales))
        {
            var orphaned = CommandResourceKeys(locale).Except(used).Order().ToList();
            if (orphaned.Count > 0)
                report.Add($"{(locale.Length == 0 ? "neutral" : locale)}: {orphaned.Count} orphaned → {string.Join(", ", orphaned.Take(5))}");
        }

        Assert.True(report.Count == 0,
            "Command labels no token asks for any more (remove them):\n" + string.Join("\n", report));
    }

    // -- VS Code manifest (vscode/package.nls*.json) ---------------------------
    //
    // The FOURTH channel of translated text, and the last one with no guard. `vscode/package.json`
    // refers to its labels through `%key%` tokens that VS Code resolves at activation against
    // `package.nls[.<locale>].json`. Nothing checked it: not `npm run typecheck`, not the VSIX
    // build, not the three rules above.
    //
    // Its failure is in the worst possible order -- silent in the repository, visible to the user:
    //   . missing from the NEUTRAL file  -> VS Code shows the raw token,
    //     "%inferpal.config.model%", in all ten languages, in the very place the user configures
    //     the product;
    //   . missing from a TRANSLATED file -> English fallback, a half-translated UI.
    //
    // It is the mirror of the defect found on 2026-09-08: the VSIX command table, the twin channel
    // on the Visual Studio side, was equally unguarded and shipped a command with no label in all
    // ten files. Measured here at ZERO violations (19 tokens x 10 bundles): free, hence locked now.

    private static string VsCodeDir() => Path.Combine(RepoRootDir(), "vscode");

    /// <summary>The <c>%key%</c> tokens the manifest asks for.</summary>
    private static HashSet<string> ManifestTokens() =>
        new(Regex.Matches(File.ReadAllText(Path.Combine(VsCodeDir(), "package.json")), @"""%([^%""]+)%""")
                 .Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);

    [Fact]
    public void EveryManifestToken_IsTranslatedInEveryNlsBundle()
    {
        var tokens  = ManifestTokens();
        var bundles = Directory.GetFiles(VsCodeDir(), "package.nls*.json").Order().ToList();

        // Two witnesses, because two things can break silently: the token pattern (it finds none,
        // and the rule passes by comparing nothing) and the bundle enumeration (a renamed folder, a
        // broken glob). The second is an EQUALITY, not a minimum: the neutral file plus the nine
        // locales, no more and no less.
        Assert.True(tokens.Count >= 10,
            $"Only {tokens.Count} %key% token(s) read from package.json: the rule compares nothing.");
        Assert.Equal(Locales.Length + 1, bundles.Count);

        var report = new List<string>();
        foreach (var bundle in bundles)
        {
            var missing = tokens.Except(BundleKeys(bundle)).Order().ToList();
            if (missing.Count > 0)
                report.Add($"{Path.GetFileName(bundle)}: {missing.Count} missing -> {string.Join(", ", missing.Take(5))}");
        }

        Assert.True(report.Count == 0,
            "VS Code manifest tokens nothing translates. Missing from the neutral file, VS Code "
            + "shows \"%key%\" verbatim in the settings; missing from a translated file, it falls "
            + "back to English:\n" + string.Join("\n", report));
    }

    [Fact]
    public void NoNlsBundle_CarriesAKeyTheManifestNoLongerAsks()
    {
        // The other direction. An orphaned entry breaks nothing -- and that is the problem: it
        // survives renames, gets translated into ten languages, and nobody displays it.
        var tokens = ManifestTokens();
        var report = new List<string>();

        foreach (var bundle in Directory.GetFiles(VsCodeDir(), "package.nls*.json").Order())
        {
            var orphaned = BundleKeys(bundle).Except(tokens).Order().ToList();
            if (orphaned.Count > 0)
                report.Add($"{Path.GetFileName(bundle)}: {orphaned.Count} orphan(s) -> {string.Join(", ", orphaned.Take(5))}");
        }

        Assert.True(report.Count == 0,
            "VS Code manifest labels no token asks for any more (remove them):\n"
            + string.Join("\n", report));
    }
}
