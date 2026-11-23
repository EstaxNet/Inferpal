using System.IO;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A coverage report (pari 173) reads as its uncovered lines per source file, the least covered first and the file open in
/// the editor before every other — lcov, Cobertura and JaCoCo through the model <c>/test coverage</c> reads. The lcov and
/// Cobertura below are what c8 wrote for a module half tested (paths made neutral); JaCoCo follows its report format.
/// </summary>
public class CoverageReportTextTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "inferpal-shop");
    private static bool InRoot(string path) => path.StartsWith(Root, StringComparison.OrdinalIgnoreCase);

    // c8 under Windows: backslashes in SF.
    private const string Lcov = "TN:\nSF:src\\cart.js\nFN:1,total\nDA:1,1\nDA:2,1\nDA:3,1\nDA:4,1\nDA:5,1\nDA:6,1\nDA:7,1\nDA:8,0\nDA:9,0\n"
                              + "DA:10,0\nDA:11,0\nDA:12,0\nDA:13,0\nDA:14,1\nDA:15,1\nLF:15\nLH:9\nend_of_record\n"
                              + "TN:\nSF:src\\tax.js\nDA:1,0\nDA:2,0\nDA:3,0\nDA:4,0\nDA:5,0\nLF:5\nLH:0\nend_of_record\n";

    private const string Cobertura = """
        <?xml version="1.0" ?>
        <!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd">
        <coverage lines-valid="20" lines-covered="9" line-rate="0.45" branches-valid="5" branches-covered="3" branch-rate="0.6" timestamp="1795470250840" complexity="0" version="0.1">
          <sources><source>/ci/build/shop</source></sources>
          <packages><package name="main" line-rate="0.45" branch-rate="0.6"><classes>
            <class name="cart.js" filename="src\cart.js" line-rate="0.6" branch-rate="0.75">
              <methods><method name="total" hits="1" signature="()V"><lines><line number="1" hits="1"/></lines></method></methods>
              <lines>
                <line number="1" hits="1"/><line number="2" hits="1"/><line number="3" hits="1"/><line number="4" hits="1"/>
                <line number="5" hits="1"/><line number="6" hits="1"/><line number="7" hits="1"/><line number="8" hits="0"/>
                <line number="9" hits="0"/><line number="10" hits="0"/><line number="11" hits="0"/><line number="12" hits="0"/>
                <line number="13" hits="0"/><line number="14" hits="1"/><line number="15" hits="1"/>
              </lines>
            </class>
            <class name="tax.js" filename="src\tax.js" line-rate="0" branch-rate="0">
              <lines><line number="1" hits="0"/><line number="2" hits="0"/><line number="3" hits="0"/><line number="4" hits="0"/><line number="5" hits="0"/></lines>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    [Theory]
    [InlineData("lcov.info", Lcov, "lcov")]
    [InlineData("coverage.cobertura.xml", Cobertura, "Cobertura")]
    public void AReport_ReadsAsItsUncoveredLines_TheLeastCoveredFirst(string name, string report, string format)
    {
        var text = CoverageReportText.Summarize(name, report, 30_000, Root, exists: InRoot)!;

        Assert.StartsWith($"[{name}: a coverage report ({format}), 2 source file(s), 9 of 20 lines covered (45.0 %).", text);
        Assert.Contains("\n## src/tax.js — 0 of 5 lines covered (0.0 %)\nnot covered: 1-5\n", text);
        Assert.Contains("\n## src/cart.js — 9 of 15 lines covered (60.0 %)\nnot covered: 8-13\n", text);
        Assert.True(text.IndexOf("src/tax.js", StringComparison.Ordinal) < text.IndexOf("src/cart.js", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFileOpenInTheEditor_ComesFirst_OrIsSaidMissing()
    {
        var text = CoverageReportText.Summarize("lcov.info", Lcov, 30_000, Root, Path.Combine(Root, "src", "cart.js"), InRoot)!;
        Assert.Contains("the file open in the editor before every other", text);
        Assert.True(text.IndexOf("src/cart.js", StringComparison.Ordinal) < text.IndexOf("src/tax.js", StringComparison.Ordinal));

        var missing = CoverageReportText.Summarize("lcov.info", Lcov, 30_000, Root, Path.Combine(Root, "src", "orders.js"), InRoot)!;
        Assert.Contains("The file open in the editor, src/orders.js, is not in this report", missing);
    }

    [Fact]
    public void AJacocoReport_IsFoundUnderItsSourceRoot()
    {
        const string jacoco = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <!DOCTYPE report PUBLIC "-//JACOCO//DTD Report 1.1//EN" "report.dtd">
            <report name="shop"><sessioninfo id="s" start="1" dump="2"/>
              <package name="com/acme/cart">
                <class name="com/acme/cart/Cart" sourcefilename="Cart.java"/>
                <sourcefile name="Cart.java">
                  <line nr="3" mi="0" ci="4" mb="0" cb="0"/><line nr="4" mi="2" ci="0" mb="0" cb="0"/>
                  <line nr="5" mi="2" ci="0" mb="0" cb="0"/><line nr="7" mi="1" ci="3" mb="1" cb="1"/>
                </sourcefile>
              </package>
            </report>
            """;
        var java = Path.Combine(Root, "src", "main", "java", "com", "acme", "cart", "Cart.java");
        var text = CoverageReportText.Summarize("jacoco.xml", jacoco, 30_000, Root, exists: p => p == java)!;
        Assert.StartsWith("[jacoco.xml: a coverage report (JaCoCo), 1 source file(s), 2 of 4 lines covered (50.0 %).", text);
        Assert.Contains("## src/main/java/com/acme/cart/Cart.java — 2 of 4 lines covered (50.0 %)\nnot covered: 4-5\n", text);
    }

    [Fact]
    public void FilesThatAreNoSourceOfTheWorkspace_AreCounted_NeverListed()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "lib.js");
        var report = Lcov + $"SF:{outside}\nDA:1,0\nend_of_record\nSF:test\\cart.test.js\nDA:1,1\nend_of_record\n";
        var text = CoverageReportText.Summarize("lcov.info", report, 30_000, Root, exists: InRoot)!;
        Assert.Contains("2 file(s) the report names are not this workspace's source", text);
        Assert.DoesNotContain("lib.js", text);
        Assert.DoesNotContain("cart.test.js", text);
    }

    [Fact]
    public void AFullPage_SaysWhatItLeavesOut_AndFullyCoveredFilesAreCounted()
    {
        var report = string.Concat(Enumerable.Range(0, 300).Select(i => $"SF:src/m{i:D3}.js\nDA:1,0\nDA:2,1\nend_of_record\n"))
                   + "SF:src/done.js\nDA:1,1\nend_of_record\n";
        var text = CoverageReportText.Summarize("lcov.info", report, 3_000, Root, exists: InRoot)!;
        Assert.Contains("more file(s) with uncovered lines, not listed: the page is full.", text);
        Assert.Contains("1 file(s) fully covered: not listed.", text);
    }

    [Fact]
    public void AModulesReport_FindsItsSourcesUnderTheModule_AndAFileFoundNowhereIsLeftOut()
    {
        var module = Path.Combine(Root, "packages", "web");
        var source = Path.Combine(module, "src", "app.ts");
        var report = "SF:src/app.ts\nDA:1,0\nend_of_record\nSF:src/gone.ts\nDA:1,0\nend_of_record\n";
        var text = CoverageReportText.Summarize("lcov.info", report, 30_000, Root, source, p => p == source,
                                                Path.Combine(module, "coverage"))!;
        Assert.Contains("## packages/web/src/app.ts — 0 of 1 lines covered", text);
        Assert.Contains("1 file(s) the report names are not this workspace's source (not found in it", text);
        Assert.DoesNotContain("The file open in the editor, packages/web/src/app.ts, is not in this report", text);
    }

    [Fact]
    public void AReportsSourceOnAShare_IsNeverAskedOfTheDisk()
    {
        if (!OperatingSystem.IsWindows()) return;   // a share is a Windows path
        var asked = new List<string>();
        var report = Cobertura.Replace("<source>/ci/build/shop</source>", @"<source>\\files.example\share</source>");
        CoverageReportText.Summarize("coverage.cobertura.xml", report, 30_000, Root, exists: p => { asked.Add(p); return false; });
        Assert.NotEmpty(asked);
        Assert.DoesNotContain(asked, p => p.StartsWith(@"\\", StringComparison.Ordinal));
    }

    [Fact]
    public void AModulesCoberturaReport_FindsItsSourcesUnderTheModule()
    {
        var module = Path.Combine(Root, "packages", "web");
        var source = Path.Combine(module, "src", "cart.js");
        var text = CoverageReportText.Summarize("coverage.cobertura.xml", Cobertura, 30_000, Root, exists: p => p == source,
                                                reportFolder: Path.Combine(module, "coverage"))!;
        Assert.Contains("## packages/web/src/cart.js — 9 of 15 lines covered", text);
        Assert.Contains("1 file(s) the report names are not this workspace's source (not found in it", text);
    }

    [Fact]
    public void ALeftOutFile_IsCountedOnce_HoweverManyClassesItHolds()
    {
        var classes = string.Concat(Enumerable.Range(0, 5).Select(i =>
            $"<class name=\"T{i}\" filename=\"tests/CartTests.cs\" line-rate=\"1\"><lines><line number=\"{i + 1}\" hits=\"1\"/></lines></class>"));
        var report = Cobertura.Replace("<class name=\"tax.js\"", classes + "<class name=\"tax.js\"");
        var text = CoverageReportText.Summarize("coverage.cobertura.xml", report, 30_000, Root, exists: InRoot)!;
        Assert.Contains(" 1 file(s) the report names are not this workspace's source", text);
    }

    [Fact]
    public void AFullyCoveredOpenFile_IsSaidSo()
    {
        var text = CoverageReportText.Summarize("lcov.info", "SF:src/done.js\nDA:1,1\nDA:2,1\nend_of_record\nSF:src/tax.js\nDA:1,0\nend_of_record\n",
                                                30_000, Root, Path.Combine(Root, "src", "done.js"), InRoot)!;
        Assert.Contains("The file open in the editor, src/done.js, is fully covered (2 lines).", text);
    }

    [Fact]
    public void ALineNumberPastAnInteger_IsSkipped_NeverThrown()
    {
        var jacoco = "<?xml version=\"1.0\"?><report name=\"s\"><sessioninfo id=\"s\" start=\"1\" dump=\"2\"/><package name=\"a\">"
                   + "<sourcefile name=\"A.java\"><line nr=\"99999999999\" mi=\"1\" ci=\"0\"/><line nr=\"2\" mi=\"1\" ci=\"0\"/></sourcefile></package></report>";
        var text = CoverageReportText.Summarize("jacoco.xml", jacoco, 30_000, Root, exists: InRoot)!;
        Assert.Contains("0 of 1 lines covered", text);
    }

    [Theory]
    [InlineData("package.json", "{\"name\":\"shop\"}")]
    [InlineData("pom.xml", "<project><modelVersion>4.0.0</modelVersion></project>")]
    [InlineData("notes.info", "Some notes about the project.")]
    public void AFileThatIsNoReport_IsReadAsItsLines(string name, string text)
    {
        Assert.Null(CoverageReportText.Summarize(name, text, 30_000, Root, exists: InRoot));
    }

    [Fact]
    public void Ranges_JoinConsecutiveLines()
    {
        Assert.Equal(["1-3", "5", "7-8"], CoverageReportText.Ranges([8, 1, 2, 3, 5, 7]));
    }
}
