using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio's half of the cut at a small window: the window's own turn decides it with its plan, sends the tools used
/// most and the ones the question named, marks what it sends for the loop, and says it once in the chat.
/// </summary>
[Collection(SignalCollection.Name)]
public class SmallWindowToolsVsTests
{
    [Fact]
    public async Task AtTheFactoryWindow_TheWindowsTurnSendsTheToolsUsedMost_AndTheOnesTheQuestionNamed()
    {
        using var vs = new VsWindowHarness(config: new InferpalConfig { DefaultModel = "m", ContextWindowSize = 8_192 });
        vs.Fake.LoadedContextWindow = 8_192;
        vs.Fake.RunAgentThroughChat = false;
        IToolRegistry? offered = null;
        // The chat loop runs through the client; the agent mode's orchestrator asks the model step by step.
        vs.Fake.OnAgentRun = (history, tools, _, _) =>
        {
            offered = tools;
            return Task.FromResult(new AgentResult("done", [], history));
        };
        vs.Fake.OnChatRequest = (_, _, tools, _) =>
        {
            if (tools.Definitions.Count > 0) offered ??= tools;
            return Task.FromResult(new ChatTurnResult("done", null, 0, 0));
        };

        await vs.AskAsync("capture the page with view_page");
        await VsWindowHarness.UntilAsync(() => offered is not null);

        var names = offered!.Definitions.Select(d => d.Function.Name).ToList();
        Assert.Contains("read_file", names);
        Assert.Contains("view_page", names);                                   // named by the question
        Assert.DoesNotContain("interact_page", names);
        Assert.Contains(vs.Tools.Definitions, d => d.Function.Name == "interact_page");   // witness: offered, then cut
        Assert.True(SmallWindowToolRegistry.IsDecided(offered));               // the loop sends what the plan made room for
        var start = Localization.Strings.MsgToolsCutSmallWindow(8_192, 1, "§", "").Split('8')[0];
        await VsWindowHarness.UntilAsync(() => vs.Window.Messages.Any(m => m.Content?.StartsWith(start, StringComparison.Ordinal) == true));
        Assert.Single(vs.Window.Messages, m => m.Content?.StartsWith(start, StringComparison.Ordinal) == true);
    }
}
