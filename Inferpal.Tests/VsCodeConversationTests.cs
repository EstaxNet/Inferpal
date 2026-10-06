using Inferpal.Localization;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The VS Code conversation shows, regenerates and exports what happened, and nothing else.
/// </summary>
/// <remarks>
/// ⚠ These rules read the extension's source (it has no TypeScript test runner). What they guard is MEASURED on the
/// real bundle by <c>docs/probes/ui-render/vscode-chat-dom.ps1</c>, which plays host messages into the webview and
/// reads the bubbles it draws.
/// </remarks>
public class VsCodeConversationTests
{
    private static string Case(string source, string label)
    {
        var at = source.IndexOf(label, StringComparison.Ordinal);
        Assert.True(at >= 0, $"\"{label}\" not found — the rule no longer measures anything.");
        var next = source.IndexOf("    case '", at + label.Length, StringComparison.Ordinal);
        return next < 0 ? source[at..] : source[at..next];
    }

    /// <summary>
    /// ⚠ A tool step sealed the stream bubble: the act's narration ("Let me read the file first.") then outlived the
    /// stream reset that drops it — shown above the answer, gone on reload — and an answer followed by a tool notice
    /// (the session recap) was drawn a second time when the turn ended.
    /// </summary>
    [Fact]
    public void AToolStep_LeavesTheStreamOpen_SoTheResetDropsTheActsNarration()
    {
        var webview = WebviewRebuildTests.TsCode("webview/main.ts");

        var tool = Case(webview, "case 'tool':");
        Assert.Contains("addToolBubble(", tool, StringComparison.Ordinal);   // WITNESS: the case is read
        Assert.True(!tool.Contains("finishStream()", StringComparison.Ordinal),
            "A tool step seals the stream bubble again: the act's narration survives the stream reset, and an answer "
            + "followed by the session recap is drawn twice.");

        // An approval card still seals what was streamed before it (it comes after it): that narration is
        // remembered and dropped at the act's reset, like the open bubble.
        var card = WebviewRebuildTests.Body(webview, "function addApprovalCard(");
        Assert.Contains("actNarration.push(streamEl)", card, StringComparison.Ordinal);
        var reset = Case(webview, "case 'streamReset':");
        Assert.Contains("streamEl.remove()", reset, StringComparison.Ordinal);
        Assert.Contains("for (const sealed of actNarration)", reset, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ A question that quotes a reasoning tag lost it on screen ("Why does it print before answering?"): the
    /// renderer strips reasoning, which only an ANSWER carries. The copy button already copied it whole.
    /// </summary>
    [Fact]
    public void AQuestion_IsShownWhole_EvenWhenItQuotesAReasoningTag()
    {
        var webview = WebviewRebuildTests.TsCode("webview/main.ts");
        var user = WebviewRebuildTests.Body(webview, "function addUser(");
        Assert.Contains("splitAttachments(item.text)", user, StringComparison.Ordinal);   // WITNESS
        Assert.Contains("renderMarkdownInto(body, text, false)", user, StringComparison.Ordinal);

        // Reference arm: an answer still loses its reasoning.
        var answer = WebviewRebuildTests.Body(webview, "function addAnswer(");
        Assert.Contains("renderMarkdownInto(body, item.text)", answer, StringComparison.Ordinal);
        var markdown = WebviewRebuildTests.TsCode("webview/markdown.ts");
        Assert.Contains("stripReasoning ? md.render(stripThinkTags(text)) : md.render(text)", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ Regenerate took the last user bubble, and VS Code shows a slash command as typed: after <c>/note</c> or
    /// <c>/commit-exec</c>, Regenerate ran the command again. It resends the last question meant for the model.
    /// </summary>
    [Fact]
    public void Regenerate_ResendsTheLastQuestionMeantForTheModel_NeverASlashCommand()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");

        var regenerate = WebviewRebuildTests.Body(provider, "private async regenerate(");
        Assert.Contains("this.send(question.text)", regenerate, StringComparison.Ordinal);   // WITNESS
        Assert.Contains("ChatViewProvider.isModelQuestion(m)", regenerate, StringComparison.Ordinal);
        Assert.DoesNotContain(".find((m) => m.role === 'user')", regenerate, StringComparison.Ordinal);

        var question = WebviewRebuildTests.Body(provider, "private static isModelQuestion(");
        Assert.Contains("!m.notice", question, StringComparison.Ordinal);
        Assert.Contains("!m.text.startsWith('/')", question, StringComparison.Ordinal);
        // A refused /explain or /review never reached the model: it is still the question to resend.
        Assert.Contains("explain|review", question, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ The export counted every user bubble as a turn — a slash command shown as typed included — and exported a
    /// thread of notices alone, which is not a conversation.
    /// </summary>
    [Fact]
    public void TheExport_SendsWhatIsANotice_AndRefusesAThreadOfNoticesAlone()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        var export = WebviewRebuildTests.Body(provider, "async exportCommand(");
        Assert.Contains("host.chatExport(", export, StringComparison.Ordinal);   // WITNESS
        Assert.Contains("if (!this.hasConversation())", export, StringComparison.Ordinal);
        Assert.Contains("notice: item.notice === true", export, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ The context ring took its window from the SETTING at start and after every save: with a server that loaded
    /// the model with a smaller window, the ring showed the conversation against a window it does not have, until the
    /// next question measured it again. The host's window (<c>context/window</c>) is the one it measures against.
    /// </summary>
    [Fact]
    public void TheContextRing_TakesTheHostsWindow_NotTheSetting()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        foreach (var site in new[] { "async onHostReady(", "async configSaved(" })
        {
            var body = WebviewRebuildTests.Body(provider, site);
            Assert.Contains("JSON.parse(await host.configGet())", body, StringComparison.Ordinal);   // WITNESS
            Assert.Contains("this.contextWindow = await host.contextWindow()", body, StringComparison.Ordinal);
            Assert.DoesNotContain("this.contextWindow = cfg.contextWindowSize ?? 0;", body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASlashCommandShownAsTyped_IsExported_ButIsNotATurn(bool asPlainText)
    {
        try
        {
            Strings.ApplyLanguage("en");
            List<ExportMessage> messages =
            [
                new("user",      "You",    "/models",           "", IsNotice: true),
                new("assistant", "m",      "qwen3:8b, devstral", ""),
                new("user",      "You",    "explain Reserve",   ""),
                new("assistant", "m",      "It reserves stock.", ""),
            ];

            var document = ConversationExporter.Build(messages, asPlainText, "m", 0, "d", "-");

            Assert.Contains("/models", document, StringComparison.Ordinal);   // what the chat showed is exported
            Assert.Contains(asPlainText ? $"{Strings.ExportTurns}: 1 " : $"| {Strings.ExportTurns} | 1 |",
                            document, StringComparison.Ordinal);
        }
        finally { Strings.ApplyLanguage(null); }
    }
}

public partial class HostServerTests
{
    [Fact]
    public async Task ChatExport_ANoticeInTheUsersPlace_IsNotCountedAsATurn()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var markdown = await h.Client.InvokeWithParameterObjectAsync<string>("chat/export", new
        {
            asPlainText = false,
            messages = new object[]
            {
                new { role = "user",      name = (string?)null, content = "/models", timestamp = "10:00", notice = true },
                new { role = "assistant", name = (string?)null, content = "list",    timestamp = "10:00" },
                new { role = "user",      name = (string?)null, content = "bonjour", timestamp = "10:01" },
                new { role = "assistant", name = (string?)null, content = "salut",   timestamp = "10:01" },
            },
        }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Contains("/models", markdown, StringComparison.Ordinal);
        // One turn, no tool call: the two "1"/"0" rows of the stats table, read by structure (labels follow the language).
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(markdown, @"^\| [^|\r\n]+ \| 1 \|\r?$",
            System.Text.RegularExpressions.RegexOptions.Multiline));
    }

    [Fact]
    public async Task ContextWindow_IsTheLoadedOne_AfterASave()
    {
        using var h = CreateHarness(cfg => cfg.ContextWindowSize = 32768);
        h.Fake.LoadedContextWindow = 4096;
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var cfg = System.Text.Json.Nodes.JsonNode.Parse(await h.Client.InvokeAsync<string>("config/get"))!.AsObject();
        cfg["contextWindowSize"] = 16384;
        await h.Client.InvokeWithParameterObjectAsync("config/update", new { json = cfg.ToJsonString() })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(4096, await h.Client.InvokeAsync<int>("context/window").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs)));
    }

    [Fact]
    public async Task ContextWindow_IsTheConfiguredOne_WhenTheServerCannotSay()
    {
        using var h = CreateHarness(cfg => cfg.ContextWindowSize = 32768);
        h.Fake.LoadedContextWindow = null;
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var cfg = System.Text.Json.Nodes.JsonNode.Parse(await h.Client.InvokeAsync<string>("config/get"))!.AsObject();
        cfg["contextWindowSize"] = 16384;
        await h.Client.InvokeWithParameterObjectAsync("config/update", new { json = cfg.ToJsonString() })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(16384, await h.Client.InvokeAsync<int>("context/window").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs)));
    }
}
