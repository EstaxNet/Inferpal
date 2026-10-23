using System.IO;
using System.Text.Json;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code's Test view: "Make This Test Pass" sends <c>/tdd</c> with the test's filter, "Explain the Failure" asks the
/// host's <c>tests/explain</c> — with the failure message the view shows, or from the test alone — as a code action.
/// </summary>
/// <remarks>The menus and the sends are held at the source: no test drives an editor. The Core's half is tested where it
/// runs (<see cref="TestFailureExplainTests"/>, <c>HostServerTests.TestsExplain_*</c>). Visual Studio's Test Explorer
/// publishes no selection to an extension: the gesture is VS Code's alone, and the docs say so.</remarks>
public sealed class TestViewFrontEndTests
{
    private static string Root => ConversationPersistenceSilenceTests.RepoRoot();

    [Fact]
    public void TheTestViewMenus_OfferBothGestures_AndKeepThemOutOfThePalette()
    {
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "vscode", "package.json")));
        var menus = package.RootElement.GetProperty("contributes").GetProperty("menus");
        static string[] Commands(JsonElement menu) => [.. menu.EnumerateArray().Select(m => m.GetProperty("command").GetString() ?? "")];

        Assert.Equal(["inferpal.fixTest", "inferpal.explainTestFailure"], Commands(menus.GetProperty("testing/item/context")));
        Assert.Equal(["inferpal.explainTestFailure"], Commands(menus.GetProperty("testing/message/context")));
        foreach (var command in new[] { "inferpal.fixTest", "inferpal.explainTestFailure" })
            Assert.Equal("false", menus.GetProperty("commandPalette").EnumerateArray()
                                       .Single(m => m.GetProperty("command").GetString() == command).GetProperty("when").GetString());
    }

    [Fact]
    public void TheGestures_SendTheTestClicked_AndItsMessage()
    {
        var provider = Path.Combine(Root, "vscode", "src", "chatViewProvider.ts");
        var gestures = File.ReadAllText(Path.Combine(Root, "vscode", "src", "testGestures.ts"));

        Assert.Contains("this.runSlashCommand(`/tdd ${testFilter(item)}`)",
                        ConversationPersistenceSilenceTests.TsMethodBody(provider, "fixTest(item: vscode.TestItem)"), StringComparison.Ordinal);
        var explain = ConversationPersistenceSilenceTests.TsMethodBody(provider, "explainTestFailure(item: vscode.TestItem, message?: vscode.TestMessage)");
        Assert.Contains("failure: message ? failureText(message) : undefined", explain, StringComparison.Ordinal);
        Assert.Contains("failureLine: message?.location ? message.location.range.start.line + 1 : undefined", explain,
                        StringComparison.Ordinal);
        Assert.Contains("'explain the test failure', true)", explain, StringComparison.Ordinal);   // a code action
        // The filter is the name without a theory's parameters: a parenthesis is syntax in a dotnet filter.
        Assert.Contains("const paren = item.label.indexOf('(');", gestures, StringComparison.Ordinal);
        // The failure as the view shows it: expected and actual travel with the text.
        Assert.Contains("message.expectedOutput", gestures, StringComparison.Ordinal);
        Assert.Contains("message.actualOutput", gestures, StringComparison.Ordinal);

        // A run takes the test's time: the person can cancel it, the cancellation reaches the host's run, and a reply
        // that crosses it is never acted on.
        Assert.Contains("ChatViewProvider.whileShown(t('Running {0}…', item.label), (token) => host.testsExplain(params, token))",
                        explain, StringComparison.Ordinal);
        var whileShown = ConversationPersistenceSilenceTests.TsMethodBody(provider, "private static whileShown<T>(");
        Assert.Contains("cancellable: true", whileShown, StringComparison.Ordinal);
        Assert.Contains("(result) => (token.isCancellationRequested ? undefined : result)", whileShown, StringComparison.Ordinal);

        // The message menu passes { test, message }; the item menu, every selected test — one is taken, and named.
        var extension = File.ReadAllText(Path.Combine(Root, "vscode", "src", "extension.ts"));
        Assert.Contains("return arg.test && chatView.explainTestFailure(arg.test, arg.message);", extension, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(extension, @"const item = oneTest\(").Count);
        Assert.Contains("t('One test at a time: {0} is the one used.', first.label)", gestures, StringComparison.Ordinal);
    }
}
