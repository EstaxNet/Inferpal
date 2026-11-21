using System.Collections.Generic;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Presentation;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The tool-off notice (pari 163): a question whose words call for a family the person turned off says so, once, and only
/// when the whole family is off; "use it for this question" offers the tools to that flow alone.
/// </summary>
public class ToolOffNoticeTests
{
    private static ToolRegistry Registry(string disabled)
    {
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        var config = new InferpalConfig { DisabledTools = disabled };
        return new ToolRegistry(editor, approval, config, new ProjectIndexService(client, config, new LspSemanticProvider()),
                                client, new ProjectMapService(editor), new McpToolService(config, approval),
                                new DocsIndexService(client, config), new OpenDocumentOverlay(), new NullDebugSession());
    }

    private const string AllOff =
        "web_search\nfetch_url\ndebug_control\ndebug_inspect\nget_debugger_state\nview_page\ninteract_page\nview_window\ninteract_window\n"
        + "query_database\nquery_data\nhttp_request\nrename_symbol\nanalyze_code\nedit_notebook\nrun_notebook\nocr\nrun_tests\n"
        + "get_diagnostics\nrun_command";

    [Theory]
    [InlineData("web", "Does Npgsql 9 change how connection strings are read? Check its release notes.")]
    [InlineData("web", "Cherche sur internet comment configurer le proxy")]
    [InlineData("web", "Was steht in den Versionshinweisen von EF Core 9?")]
    [InlineData("web", "リリースノートを確認して")]
    [InlineData("debugger", "Set a breakpoint in Total and tell me what lines holds")]
    [InlineData("debugger", "Débogue la méthode Total")]
    [InlineData("debugger", "Поставь точку останова в Total")]
    [InlineData("debugger", "在 Total 设置断点")]
    [InlineData("pages", "In the browser the total shows 0, why?")]
    [InlineData("pages", "Dans le navigateur le total affiche 0")]
    [InlineData("pages", "En el navegador el total sale en 0")]
    [InlineData("desktop", "The WPF window freezes when I press Save")]
    [InlineData("desktop", "La boîte de dialogue ne se ferme pas")]
    [InlineData("desktop", "La finestra di dialogo non si chiude")]
    [InlineData("data", "Which customers in the database have no orders?")]
    [InlineData("data", "Écris une requête SQL pour les clients sans commande")]
    [InlineData("data", "Welche Kunden in der Datenbank haben keine Bestellung?")]
    [InlineData("data", "데이터베이스에서 주문 없는 고객은?")]
    [InlineData("api", "Call the API and check the total it returns")]
    [InlineData("api", "Envoie une requête POST /orders et regarde la réponse")]
    [InlineData("api", "Wyślij żądanie do serwera")]
    [InlineData("refactoring", "Rename Total to GrandTotal everywhere")]
    [InlineData("refactoring", "Qui appelle Inventory.Reserve ?")]
    [InlineData("refactoring", "Кто вызывает Reserve?")]
    [InlineData("documents", "Fix the second cell of analysis.ipynb")]
    [InlineData("documents", "Lis le texte de l'image scannée")]
    [InlineData("documents", "この画像の文字を読んで: スキャンした請求書")]
    [InlineData("tests", "Run the tests and fix what fails")]
    [InlineData("tests", "Lance les tests")]
    [InlineData("tests", "Uruchom testy")]
    [InlineData("commands", "npm install dayjs and use it")]
    [InlineData("commands", "Lance la commande de migration")]
    [InlineData("commands", "运行命令 npm run build")]
    public void AQuestionThatCallsForAFamilyTurnedOff_SaysSo_Once(string family, string question)
    {
        var profile = Registry(AllOff).Profile();
        var said = new HashSet<string>();
        var found = ToolOffNotice.For(question, profile, said);
        Assert.NotNull(found);
        Assert.Equal(family, found!.Key);
        Assert.Contains(found.Tools[0], found.Text);
        Assert.Null(ToolOffNotice.For(question, profile, said));        // said once per conversation
        ToolOffNotice.Forget(said);
        Assert.NotNull(ToolOffNotice.For(question, profile, said));     // a new conversation is told again
    }

    [Theory]
    [InlineData("Why does Total round wrong?")]
    [InlineData("Explique ce que fait ce fichier")]
    [InlineData("Make the page load faster")]                // "page" alone is not the browser
    [InlineData("Add a test for Total")]                      // writing a test is not running it
    [InlineData("Rename this variable")]                      // one rename, not a refactoring everywhere
    [InlineData("La base du code est en C#")]                 // "base" is not a database
    [InlineData("Add validation to the endpoint")]            // an endpoint written, not called
    [InlineData("Rename the database field to CustomerId")]   // a database in the code, not queried
    [InlineData("노트북이 느려요")]                             // "laptop"
    [InlineData("扫描代码里的漏洞")]                             // scanning code, not a scanned page
    public void AQuestionThatCallsForNoFamily_SaysNothing(string question) =>
        Assert.Null(ToolOffNotice.For(question, Registry(AllOff).Profile(), new HashSet<string>()));

    [Fact]
    public void AFamilyWithOneToolStillOn_SaysNothing()
    {
        // Reference arm: the same question, the whole family off, says it.
        const string question = "Check the release notes of Npgsql 9";
        Assert.NotNull(ToolOffNotice.For(question, Registry("web_search\nfetch_url").Profile(), new HashSet<string>()));
        Assert.Null(ToolOffNotice.For(question, Registry("web_search").Profile(), new HashSet<string>()));   // fetch_url on
        Assert.Null(ToolOffNotice.For(question, Registry("").Profile(), new HashSet<string>()));
    }

    [Fact]
    public void UsedForThisQuestion_TheToolsAreOffered_ThenOffAgain()
    {
        var registry = Registry("web_search\nfetch_url");
        bool Sent(string tool) => registry.Definitions.Any(d => d.Function.Name == tool);
        Assert.False(Sent("web_search"));
        using (ToolSwitches.OpenForThisQuestion(["web_search", "fetch_url"]))
        {
            Assert.True(Sent("web_search"));
            Assert.True(Sent("fetch_url"));
            Assert.Null(registry.TurnedOffRefusal("web_search"));
        }
        Assert.False(Sent("web_search"));
        Assert.NotNull(registry.TurnedOffRefusal("web_search"));
    }

    [Fact]
    public void EveryFamily_IsCovered()
    {
        // Witness: the families walked above are the ones the notice knows.
        Assert.Equal(["web", "debugger", "pages", "desktop", "data", "api", "refactoring", "documents", "tests", "commands"], ToolOffNotice.Keys);
    }
}
