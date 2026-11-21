using System.IO;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A SARIF report reads first as what it found (pari 154): its tool, its results errors first at their file, line and
/// column, the rules met; a suppressed result counted, never listed; any other JSON read as its lines.
/// </summary>
public class SarifTextTests
{
    private const string Report = """
        {
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "CodeQL", "semanticVersion": "2.19.0",
              "rules": [
                { "id": "cs/sql-injection", "shortDescription": { "text": "SQL query built from user-controlled sources" },
                  "helpUri": "https://codeql.github.com/sql", "defaultConfiguration": { "level": "error" } },
                { "id": "cs/useless-assignment", "shortDescription": { "text": "Useless assignment to local variable" },
                  "messageStrings": { "default": { "text": "This assignment to 'x' is useless." } } }
              ] } },
            "results": [
              { "ruleId": "cs/useless-assignment", "ruleIndex": 1, "message": { "id": "default" },
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/Shop/Cart.cs" }, "region": { "startLine": 40, "startColumn": 9 } } }] },
              { "ruleIndex": 0, "message": { "text": "Query built from 'name'." },
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/Shop/Orders%20Repo.cs" }, "region": { "startLine": 12 } } }] },
              { "ruleId": "cs/useless-assignment", "level": "note", "message": { "text": "Kept on purpose." },
                "suppressions": [{ "kind": "inSource" }],
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/Shop/Legacy.cs" }, "region": { "startLine": 3 } } }] }
            ]
          }]
        }
        """;

    [Fact]
    public void AReport_ReadsAsWhatItFound_ErrorsFirst_SuppressedCountedNotListed()
    {
        var text = SarifText.Summarize("codeql.sarif", Report, 8000)!;
        Assert.StartsWith("SARIF report codeql.sarif: CodeQL 2.19.0. 2 results (1 error, 1 warning); 1 suppressed", text);
        // Its level from its rule; its message from the rule's strings; its path unescaped.
        Assert.True(text.IndexOf("src/Shop/Orders Repo.cs:12  cs/sql-injection  Query built from 'name'.", StringComparison.Ordinal)
                    < text.IndexOf("src/Shop/Cart.cs:40:9  cs/useless-assignment  This assignment to 'x' is useless.", StringComparison.Ordinal));
        Assert.DoesNotContain("Legacy.cs", text);
        Assert.Contains("cs/sql-injection: SQL query built from user-controlled sources (https://codeql.github.com/sql)", text);
    }

    [Fact]
    public void AReportPastItsBudget_CountsWhatItLeavesOut()
    {
        var text = SarifText.Summarize("codeql.sarif", Report, 260)!;
        Assert.Contains("more result", text);
    }

    [Fact]
    public void AnyOtherJson_IsReadAsItsLines() =>
        Assert.Null(SarifText.Summarize("package.json", """{ "name": "shop", "version": "1.0.0" }""", 8000));

    [Fact]
    public void TheCompilersOwnReport_Reads()
    {
        // A SARIF the C# compiler wrote (ErrorLog, version 2.1): its rules and its results' shape as found in the wild.
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "sarif", "csc.sarif");
        var text = SarifText.Summarize("csc.sarif", File.ReadAllText(path), 8000)!;
        Assert.Contains("CS8602", text);
        Assert.Contains("Program.cs:8:", text);
        Assert.DoesNotContain("CS0219", text);   // suppressed by #pragma: counted, not listed
    }

    [Fact]
    public void TheCompilersDefaultReport_Version1_Reads()
    {
        // What dotnet build -p:ErrorLog writes without ",version=2.1": SARIF 1.0, its message a string, its rules a dictionary.
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "sarif", "csc-v1.sarif");
        var text = SarifText.Summarize("build.sarif", File.ReadAllText(path), 8000)!;
        Assert.Contains("CS8602", text);
        Assert.Contains("Program.cs:8:27", text);
        Assert.DoesNotContain("CS0219", text[..text.IndexOf("Rules:", StringComparison.Ordinal)]);   // suppressedInSource
        Assert.Contains("1 suppressed", text);
    }

    private const string Extensions = """
        { "version": "2.1.0", "runs": [{
          "tool": { "driver": { "name": "CodeQL", "rules": [] },
                    "extensions": [{ "name": "csharp-queries", "rules": [
                      { "id": "cs/path-injection", "defaultConfiguration": { "level": "error" },
                        "messageStrings": { "default": { "text": "Path built from {0}." } } } ] }] },
          "originalUriBaseIds": { "SRCROOT": { "uri": "file:///C:/repo/sub/" } },
          "results": [
            { "ruleIndex": 0, "rule": { "toolComponent": { "index": 0 } },
              "message": { "id": "default", "arguments": ["'name'"] },
              "relatedLocations": [],
              "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "Api/Files.cs", "uriBaseId": "SRCROOT" }, "region": { "startLine": 10 } } }] },
            { "ruleId": "cs/path-injection", "message": { "text": "Depends on [this input](1)." },
              "relatedLocations": [{ "id": 1, "physicalLocation": { "artifactLocation": { "uri": "Api/Input.cs" }, "region": { "startLine": 4 } } }],
              "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "Api/Files.cs" }, "region": { "startLine": 8 } } }] },
            { "ruleId": "cs/path-injection", "kind": "pass", "message": { "text": "ok" } },
            { "ruleId": "cs/path-injection", "baselineState": "absent", "message": { "text": "gone" } },
            { "ruleId": "cs/path-injection", "suppressions": [{ "kind": "inSource", "status": "rejected" }], "message": { "text": "still here" },
              "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "Api/Z.cs" }, "region": { "startLine": 1 } } }] }
          ] }] }
        """;

    [Fact]
    public void ARuleOfAnExtension_ItsLevelAndMessage_AndEachStateRead()
    {
        var text = SarifText.Summarize("codeql.sarif", Extensions, 8000)!;
        Assert.Contains("3 results (3 errors)", text);                       // the extension's level, by index and by id
        Assert.Contains("1 fixed since the baseline", text);
        Assert.Contains("1 checks that passed or did not apply", text);
        Assert.Contains("C:/repo/sub/Api/Files.cs".Replace('/', Path.DirectorySeparatorChar)[..2], text);
        Assert.Contains("Path built from 'name'.", text);                    // {0} filled
        Assert.Contains("Depends on this input (Api/Input.cs:4).", text);    // the link given its place
        Assert.Contains("still here", text);                                 // a rejected suppression is no suppression
        // In one file, line 8 before line 10.
        Assert.True(text.IndexOf("Api/Files.cs:8", StringComparison.Ordinal) < text.IndexOf("Files.cs:10", StringComparison.Ordinal));
    }

    [Fact]
    public void AJsonThatOnlyMentionsSarif_IsReadAsItsLines() =>
        Assert.Null(SarifText.Summarize("workflow.json", """{ "name": "upload-sarif", "runs": [{ "step": 1 }] }""", 8000));
}
