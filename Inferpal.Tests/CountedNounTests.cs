using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Execution;
using Inferpal.Services.Presentation;
using Inferpal.Services.Rag;
using Inferpal.Services.Docs;
using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A count and the noun it counts agree in the interface language: "1 file", "2 files", in French "0 fichier", in Russian
/// "21 файл", "22 файла", "25 файлов". Expectations are literals: reading the resource would turn the test green on a
/// sentence left wrong.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class CountedNounTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 15, 30, 0);

    private static IndexSnapshot Snapshot(bool indexing = false, int done = 0, int total = 0, bool completed = false,
                                          int files = 0, int chunks = 0, int unembedded = 0, string? model = null) =>
        new("C:/repo", indexing, done, total, completed, files, chunks, unembedded, 0, [], null,
            model, false, new DateTime(2026, 10, 8, 10, 0, 0), null, false);

    private static T In<T>(string language, Func<T> read)
    {
        try { Strings.ApplyLanguage(language); return read(); }
        finally { Strings.ApplyLanguage(null); }
    }

    private static string IndexDetail(string language, int files) =>
        In(language, () => SettingsWidgets.IndexCard(Snapshot(completed: true, files: files), true, null, Now).Detail);

    [Fact]
    public void TheIndexCard_CountsOneFile_AsAFile()
    {
        Assert.StartsWith("1 file · ", IndexDetail("en", 1));
        Assert.StartsWith("2 files · ", IndexDetail("en", 2));
        Assert.StartsWith("0 files · ", IndexDetail("en", 0));
        // French counts zero in the singular.
        Assert.StartsWith("0 fichier · ", IndexDetail("fr", 0));
        Assert.StartsWith("1 fichier · ", IndexDetail("fr", 1));
        Assert.StartsWith("2 fichiers · ", IndexDetail("fr", 2));
        // The number keeps its group separator.
        Assert.StartsWith(1234.ToString("N0") + " files · ", IndexDetail("en", 1234));
    }

    [Fact]
    public void TheIndexCard_CountsTheTotalOfAPassThatHasOneFile()
    {
        var progress = In("en", () => SettingsWidgets.IndexCard(Snapshot(indexing: true, done: 0, total: 1), true, null, Now).Detail);
        Assert.Equal("0 of 1 file read", progress);
        var more = In("en", () => SettingsWidgets.IndexCard(Snapshot(indexing: true, done: 3, total: 40), true, null, Now).Detail);
        Assert.Equal("3 of 40 files read", more);
        // Russian picks the genitive that follows "из" for the number.
        Assert.Equal("Прочитано 0 из 1 файла",
            In("ru", () => SettingsWidgets.IndexCard(Snapshot(indexing: true, done: 0, total: 1), true, null, Now).Detail));
        Assert.Equal("Прочитано 3 из 40 файлов",
            In("ru", () => SettingsWidgets.IndexCard(Snapshot(indexing: true, done: 3, total: 40), true, null, Now).Detail));
    }

    [Fact]
    public void AServerWithOneTool_IsConnectedWithOneTool()
    {
        string Status(int tools) => In("en", () => McpServerCards.Build(
            [new McpServerConfig("one", "npx", [], new Dictionary<string, string>())],
            [new McpServerStatus("one", Connected: true, ToolCount: tools, Error: null)])[0].StatusText);

        Assert.Equal("Connected · 1 tool", Status(1));
        Assert.Equal("Connected · 3 tools", Status(3));
    }

    [Fact]
    public void ADocsSiteOfOnePage_SaysOnePage()
    {
        var site = DocSite.Create("https://example.org/docs", "Example");
        string Row(int pages, int holes, Func<DocsSiteRow, string> read) => In("en", () => read(SettingsWidgets.DocsSites(
            [site], [(site, pages, pages * 4)], new Dictionary<string, int> { [site.Id] = holes }, indexingId: null)[0]));

        Assert.Equal("1 page · indexed", Row(1, 0, r => r.Status));
        Assert.Equal("5 pages · indexed", Row(5, 0, r => r.Status));
        Assert.Equal("1 page · 1 passage without search by meaning", Row(1, 1, r => r.Status));
        // The second figure counts passages, not pages: a site of 3 pages can have 25 without a vector.
        Assert.Equal("3 pages · 25 passages without search by meaning", Row(3, 25, r => r.Status));
        Assert.StartsWith("1 passage was read while", Row(1, 1, r => r.HoleNote));
        Assert.StartsWith("2 passages were read while", Row(1, 2, r => r.HoleNote));
    }

    [Fact]
    public void ACreatedFileOfOneLine_IsOneLine()
    {
        string Meta(string content) => In("en", () =>
            ApprovalCard.Build(new ApprovalPrompt("write_file", "a.cs", "a.cs", new DiffInfo("", content, "a.cs"), "m"), null).Meta);
        string More(int lines) => In("en", () =>
            ApprovalCard.Build(new ApprovalPrompt("write_file", "a.cs", "a.cs",
                new DiffInfo("", string.Concat(Enumerable.Range(0, lines).Select(i => $"line {i}\n")), "a.cs"), "m"), null).More);

        Assert.Equal("1 line", Meta("only line\n"));
        Assert.Equal("2 lines", Meta("a\nb\n"));
        Assert.Equal("… 1 more line", More(ApprovalCard.PreviewLines + 1));
        Assert.Equal("… 2 more lines", More(ApprovalCard.PreviewLines + 2));
    }

    [Fact]
    public void ARun_CountsItsSteps_InEveryLanguage()
    {
        RunSummaryModel Run(int reads, int commands) => RunSummary.Build(
            [.. Enumerable.Range(0, reads).Select(i => new ToolExecution("read_file", $"{{\"path\":\"f{i}.cs\"}}", "…")),
             .. Enumerable.Repeat(new ToolExecution("run_command", "{}", "ok"), commands)], null)!;

        Assert.Equal("2 steps", In("en", () => Run(1, 1).Title));
        Assert.Equal("read 1 file · ran 1 command", In("en", () => Run(1, 1).Detail));
        Assert.Equal("1 step", In("en", () => Run(1, 0).Title));
        Assert.Equal("read 3 files · ran 2 commands", In("en", () => Run(3, 2).Detail));
        // Russian has three forms; 21 counts like 1, 22 like 2, 25 like 5.
        Assert.Equal("21 шаг", In("ru", () => Run(21, 0).Title));
        Assert.Equal("22 шага", In("ru", () => Run(22, 0).Title));
        Assert.Equal("25 шагов", In("ru", () => Run(25, 0).Title));
        Assert.Equal("прочитан 21 файл", In("ru", () => Run(21, 0).Detail));
        // Polish has one singular only: 21 is "many".
        Assert.Equal("21 kroków", In("pl", () => Run(21, 0).Title));
        Assert.Equal("22 kroki", In("pl", () => Run(22, 0).Title));
    }

    [Fact]
    public void TheSessionList_AndTheImpactFooter_CountOne()
    {
        Assert.Equal("1 message", In("en", () => Strings.HistoryMessageCount(1)));
        Assert.Equal("4 messages", In("en", () => Strings.HistoryMessageCount(4)));
        Assert.Equal("📊 Blast radius: 1 direct · 0 transitive · 1 test · 1 entry point",
                     In("en", () => Strings.ImpactFooter(1, 0, 1, 1)));
        Assert.Equal("📊 Blast radius: 2 direct · 3 transitive · 0 tests · 2 entry points",
                     In("en", () => Strings.ImpactFooter(2, 3, 0, 2)));
    }

    // ── The class: the rule belongs to the language, the forms to the resource ─────────────────────────────────

    [Theory]
    [InlineData("en", 2, "0:1 1:0 2:1 11:1 21:1")]
    [InlineData("de", 2, "0:1 1:0 2:1")]
    [InlineData("fr", 2, "0:0 1:0 2:1 21:1")]
    [InlineData("ru", 3, "1:0 2:1 4:1 5:2 11:2 12:2 14:2 21:0 22:1 25:2 101:0 111:2 112:2")]
    [InlineData("pl", 3, "1:0 2:1 4:1 5:2 12:2 21:2 22:1 25:2 101:2")]
    [InlineData("ja", 1, "0:0 1:0 2:0")]
    // An interface language without its own resources reads the English ones: two forms, counted as English does.
    [InlineData("uk", 2, "1:0 2:1 21:1")]
    public void EachLanguage_PicksItsForm(string language, int forms, string expected)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
        foreach (var pair in expected.Split(' '))
        {
            var (n, index) = (long.Parse(pair.Split(':')[0]), int.Parse(pair.Split(':')[1]));
            Assert.True(CountedForms.Index(culture, n, forms) == index, $"{language}: {n} should take form {index}");
        }
    }

    [Fact]
    public void ASelector_LeavesEveryOtherPlaceholder_AsStringFormatWrites()
    {
        var at = new DateTime(2026, 10, 8, 9, 5, 0);
        var plain  = string.Format("{0:N0} · {1:t} · {2}", 12345, at, "x");
        var shared = string.Format(CountedForms.Instance, "{0:N0} · {1:t} · {2}", 12345, at, "x");
        Assert.Equal(plain, shared);
        // A number that is not whole reads in the plural.
        Assert.Equal("1.5 files", string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} ", 1.5)
                                  + string.Format(CountedForms.Instance, "{0:file|files}", 1.5));
    }

    private static readonly Dictionary<string, int> FormsOf = new()
    {
        [""] = 2, [".fr"] = 2, [".de"] = 2, [".es"] = 2, [".it"] = 2, [".ru"] = 3, [".pl"] = 3,
        [".ja"] = 1, [".ko"] = 1, [".zh-CN"] = 1,
    };

    private static readonly System.Text.RegularExpressions.Regex Selector =
        new(@"\{(\d+):([^}]*\|[^}]*)\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static Dictionary<string, string> Resources(string suffix)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return System.Xml.Linq.XDocument.Load(Path.Combine(dir!, "Inferpal.Core", "Localization", $"Strings{suffix}.resx"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => d.Element("value")?.Value ?? "");
    }

    [Fact]
    public void EverySelector_GivesTheFormsItsLanguageCounts()
    {
        var wrong = new List<string>();
        var selectors = 0;
        foreach (var (suffix, forms) in FormsOf)
            foreach (var (key, value) in Resources(suffix))
                foreach (System.Text.RegularExpressions.Match m in Selector.Matches(value))
                {
                    selectors++;
                    var given = m.Groups[2].Value.Split('|').Length;
                    if (given != forms)
                        wrong.Add($"Strings{suffix}.resx {key}: {m.Value} gives {given} forms, the language counts {forms}");
                }

        // Witness: the reading is alive — the migrated resources carry selectors in several languages.
        Assert.True(selectors >= 50, $"only {selectors} selectors read: is the pattern still the resources' syntax?");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void ATranslation_SelectsWhereTheEnglishSelects_OrWritesALabel()
    {
        var english = Resources("");
        var wrong = new List<string>();
        foreach (var (suffix, forms) in FormsOf.Where(f => f.Key != "" && f.Value > 1))
            foreach (var (key, value) in Resources(suffix))
            {
                if (!english.TryGetValue(key, out var source)) continue;
                foreach (var index in Selector.Matches(source).Select(m => m.Groups[1].Value).Distinct())
                {
                    // Each place the number is written: followed by its selector, or by no word at all ("Файлов: {0}").
                    var written = new System.Text.RegularExpressions.Regex(@"\{" + index + @"(?::[^}|]*)?\}(?<after>\s*(?:\{"
                                                                           + index + @":[^}]*\|[^}]*\}|\p{L}))?");
                    foreach (System.Text.RegularExpressions.Match m in written.Matches(value))
                        if (m.Groups["after"].Success && !m.Groups["after"].Value.TrimStart().StartsWith('{'))
                            wrong.Add($"Strings{suffix}.resx {key}: {{{index}}} is followed by a word, with no selector — '{value}'");
                }
            }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void TheSingular_IsNeverASecondResourceTheCallerChooses()
    {
        // "count == 1 ? X1 : X(count)" put the rule in every caller, and in English: French counts zero in the
        // singular, Russian 21. The rule now lives in CountedForms; a resource "X1" beside "X" brings the caller back.
        var english = Resources("");
        var pairs = english.Keys.Where(k => k.EndsWith('1') && english.ContainsKey(k[..^1])).ToList();
        Assert.True(english.Count > 500, "the neutral resources were not read");
        Assert.True(pairs.Count == 0, "singular resources beside their plural: " + string.Join(", ", pairs));
    }
}
