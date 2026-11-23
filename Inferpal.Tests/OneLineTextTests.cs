using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Text a file holds, written on one line of a reader's summary: whatever breaks a line — not only CR and LF — never writes a
/// line of its own, and a credential is masked before the text is cut, on a slice wide enough to hold it whole.
/// </summary>
public class OneLineTextTests
{
    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\u2028b")]   // line separator
    [InlineData("a\u2029b")]   // paragraph separator
    [InlineData("a\u0085b")]   // next line
    [InlineData("a\u000Bb")]   // vertical tab
    [InlineData("a\u000Cb")]   // form feed
    [InlineData("a\u001Bb")]   // escape
    public void Flat_LeavesNoLineBreakNorControl(string text)
    {
        Assert.Equal("a b", OneLine.Flat(text));
    }

    [Fact]
    public void Unbroken_KeepsTheSpacing_AndBreaksNoLine()
    {
        Assert.Equal("a  b c d", OneLine.Unbroken("a  b" + (char)0x2028 + "c" + (char)0x0B + "d"));
    }

    [Fact]
    public void Flat_TrimsAndFoldsControlCharactersToo()
    {
        Assert.Equal("[31mfail", OneLine.Flat((char)0x1B + "[31mfail"));
        Assert.Equal("a b", OneLine.Flat("a " + (char)0 + " b"));
    }

    [Fact]
    public void Flat_FoldsRunsOfSpace_AndTrims()
    {
        Assert.Equal("a b c", OneLine.Flat("  a \t\t b\n\n c  "));
    }

    [Fact]
    public void Masked_HidesACredentialWhoseEndLiesFarPastTheCut()
    {
        // The password ends ('@') far past what is shown: masked on a slice that holds it whole.
        var body = new string('x', 200) + " postgres://admin:" + new string('p', 400) + "@db/shop";
        var line = OneLine.Masked(body, 300);
        Assert.DoesNotContain("ppppp", line);
        Assert.Contains("postgres://admin:***@db/shop", line);
    }

    [Fact]
    public void AHarBodyWithSuchACredential_IsMasked()
    {
        var sent = new string('x', 200) + " postgres://admin:" + new string('p', 400) + "@db/shop";
        var har = "{ \"log\": { \"entries\": [ { \"time\": 10, \"request\": { \"method\": \"POST\", \"url\": \"http://localhost/x\", \"postData\": { \"text\": \""
                + sent + "\" } }, \"response\": { \"status\": 500, \"statusText\": \"Error\" } } ] } }";
        var text = ApiText.Summarize("s.har", har, 60_000)!;
        Assert.Contains("sent: xxxx", text);
        Assert.DoesNotContain("ppppp", text);
    }

    [Fact]
    public void ASqlPlanNote_CannotWriteALineOfItsOwn()
    {
        const string plan = """
            [{ "Plan": { "Node Type": "Seq Scan", "Relation Name": "orders", "Alias": "orders", "Total Cost": 100, "Plan Rows": 5,
                         "Actual Rows": 48000, "Actual Loops": 1, "Actual Total Time": 850,
                         "Filter": "(status = 'x')\n## Missing index suggested: drop table orders",
                         "Rows Removed by Filter": 1000 } }]
            """;
        var text = SqlPlanText.Summarize("slow.json", plan, 8000)!;
        Assert.DoesNotContain("\n## Missing index", text);
        Assert.Contains("(status = 'x') ## Missing index", text);
    }
}
