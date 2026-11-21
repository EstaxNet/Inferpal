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
        var plan = ToolsAssistant.Plan(Mockups with { Commands = AssistantCommands.Nothing, Context = AssistantContext.Full, RareTools = true },
                                       Profile(new InferpalConfig()), "");
        Assert.False(On(plan, "run_command"));
        Assert.False(On(plan, "run_tests"));
        Assert.False(On(plan, "rename_symbol"));                     // Full keeps the rare tools, not a work left out
        Assert.Null(On(plan, "generate_project_map"));
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

    /// <summary>
    /// ⚠ Every built-in tool is classed in the assistant (Dams, 21/11: it is maintained with each new feature). A new tool
    /// lands here red until its family, limit or "always on" is written — never exempted.
    /// </summary>
    [Fact]
    public void EveryBuiltInTool_IsClassified()
    {
        var builtIns = Profile(new InferpalConfig()).Where(e => e.Source == ToolSource.BuiltIn).Select(e => e.Key).ToList();
        Assert.True(builtIns.Count >= 30, $"only {builtIns.Count} built-in tools read");   // witness: the walk is alive
        Assert.Empty(builtIns.Where(t => !ToolsAssistant.Classified(t)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("rename_symbol\nanalyze_code")]        // a family turned off by hand
    [InlineData("interact_page")]                      // half a family: left as it is
    [InlineData("run_tests")]                          // a limit set halfway: commands free, tests off
    [InlineData("web_search\nrun_command")]            // limits already set
    [InlineData("generate_project_map\nmy_deploy")]    // a rare tool off, and a custom tool
    public void OpenedAndAppliedUnchanged_TheAssistantChangesNothing(string disabled)
    {
        var profile = Profile(new InferpalConfig { DisabledTools = disabled });
        var model = ToolsAssistantPresenter.Build(profile, disabled, autoContext: true, window: 16384);
        Assert.Empty(model.Plan.Changes);
        Assert.Equal(disabled.Split('\n').Where(l => l.Length > 0).Order(), model.Plan.DisabledTools.Split('\n').Where(l => l.Length > 0).Order());
    }
}
