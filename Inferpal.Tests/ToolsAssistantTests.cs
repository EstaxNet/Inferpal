using System.Collections.Generic;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Presentation;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The tools assistant's decision (pari 163): a family not picked is turned off, the limits on reach hold, every line it
/// does not decide is kept, and the request it leaves is lighter — what the review announces is what a request then weighs.
/// </summary>
public class ToolsAssistantTests
{
    private static IReadOnlyList<ToolProfileEntry> Profile(InferpalConfig config)
    {
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        return new ToolRegistry(editor, approval, config, new ProjectIndexService(client, config, new LspSemanticProvider()),
                                client, new ProjectMapService(editor), new McpToolService(config, approval),
                                new DocsIndexService(client, config), new OpenDocumentOverlay(), new NullDebugSession()).Profile();
    }

    // The scenario of the mock-ups: code, tests and data; free commands; only the pages given; no debugger.
    private static readonly AssistantAnswers Mockups = new(
        AssistantWork.Code | AssistantWork.Tests | AssistantWork.Data, AssistantCommands.Free, AssistantInternet.GivenPages,
        AssistantDebugger.Never, AssistantContext.Balanced, new Dictionary<string, bool>());

    private static bool? On(AssistantPlan plan, string tool) => plan.Changes.FirstOrDefault(c => c.Key == tool)?.On;

    [Fact]
    public void AFamilyNotPicked_IsTurnedOff_ThePickedOnesAndTheLimitsHold()
    {
        var plan = ToolsAssistant.Plan(Mockups, Profile(new InferpalConfig()), "");
        Assert.False(On(plan, "view_page"));
        Assert.False(On(plan, "rename_symbol"));
        Assert.False(On(plan, "web_search"));                        // a limit the person set
        Assert.Null(On(plan, "fetch_url"));                          // kept on: the person's own @url needs it
        Assert.Null(On(plan, "query_database"));                     // picked: on, no change
        Assert.Null(On(plan, "run_command"));
        Assert.Null(On(plan, "update_memory"));                      // undecided: left as it is
        Assert.Contains("web_search", plan.DisabledTools.Split('\n'));
        Assert.True(plan.ToolTokensAfter < plan.ToolTokensBefore * 0.7, $"{plan.ToolTokensAfter} vs {plan.ToolTokensBefore}");
    }

    [Fact]
    public void EveryLineTheAssistantDoesNotDecide_IsKept_AndADecidedOneMayComeBack()
    {
        var plan = ToolsAssistant.Plan(Mockups with { Work = Mockups.Work | AssistantWork.Refactoring },
                                       Profile(new InferpalConfig { DisabledTools = "rename_symbol" }), "my_deploy\n# a note\nrename_symbol");
        var disabled = plan.DisabledTools.Split('\n');
        Assert.Contains("my_deploy", disabled);
        Assert.Contains("# a note", disabled);
        Assert.DoesNotContain("rename_symbol", disabled);            // refactoring picked: back on
        Assert.True(On(plan, "rename_symbol"));
    }

    [Fact]
    public void NothingRuns_TurnsCommandsAndBuildsOff_AndFull_KeepsEveryFamily()
    {
        var plan = ToolsAssistant.Plan(Mockups with { Commands = AssistantCommands.Nothing, Context = AssistantContext.Full },
                                       Profile(new InferpalConfig()), "");
        Assert.False(On(plan, "run_command"));
        Assert.False(On(plan, "run_tests"));
        Assert.Null(On(plan, "rename_symbol"));                      // Full: every family on, as it is now
        Assert.True(plan.AutoContext);
    }

    [Fact]
    public void AppliedAgain_TheSameAnswers_ChangeNothing_AndWeighWhatTheReviewSaid()
    {
        var first = ToolsAssistant.Plan(Mockups, Profile(new InferpalConfig()), "");
        var applied = new InferpalConfig { DisabledTools = first.DisabledTools };
        var again = ToolsAssistant.Plan(Mockups, Profile(applied), applied.DisabledTools);
        Assert.Empty(again.Changes);
        Assert.Equal(first.ToolTokensAfter, again.ToolTokensBefore);
        Assert.Equal((first.On, first.Off), (again.On, again.Off));
    }
}
