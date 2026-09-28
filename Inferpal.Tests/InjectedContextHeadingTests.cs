using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.Agent;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The user's message keeps its own heading behind the blocks the product injects, and the agent's
//  plan may have a single step.
//
//  The first question reached the model as "## Workspace context (auto-injected…)" + the solution,
//  then the user's words WITHOUT a heading, then the plan prompt demanding 2–6 steps. Asked to
//  remember a fact — one update_memory call — Devstral and Muse planned an analysis of the workspace
//  and never wrote the memory; neither change alone made them plan it, the two together did.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class InjectedContextHeadingTests
{
    private const string Request = "Remember this for all future sessions: tests are written with xUnit.";

    [Fact]
    public void BehindAnInjectedBlock_TheUsersMessageHasItsOwnHeading()
    {
        var workspace = WorkspaceContext.Compose("Solution : Shop.slnx", null);

        var sent = ChatTurnPolicy.WithInjectedContext(Request, null, workspace);

        Assert.Equal(workspace + "\n\n" + ChatTurnPolicy.UserRequestHeader + "\n\n" + Request, sent);
    }

    [Fact]
    public void BothBlocks_KeepTheirOrder_AndTheHeadingComesOnce()
    {
        var sent = ChatTurnPolicy.WithInjectedContext(Request, "## Relevant code\n\nA", "## Workspace context\n\nB");

        Assert.Equal("## Relevant code\n\nA\n\n## Workspace context\n\nB\n\n## User request\n\n" + Request, sent);
    }

    [Fact]
    public void WithNothingInjected_TheMessageIsUnchanged()
    {
        // Reference arm: an ordinary turn gains no heading.
        Assert.Equal(Request, ChatTurnPolicy.WithInjectedContext(Request, null, ""));
        Assert.Equal(Request, ChatTurnPolicy.WithInjectedContext(Request));
    }

    [Fact]
    public void BothFrontEnds_JoinTheirBlocksThroughTheOneComposer()
    {
        var root = RepoRoot();
        foreach (var relative in new[] { Path.Combine("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"),
                                         Path.Combine("Inferpal.Host", "HostServer.cs") })
        {
            var code = ConventionCoverageTests.CodeOnly(Path.Combine(root, relative));
            Assert.Contains("ChatTurnPolicy.WithInjectedContext(", code, StringComparison.Ordinal);
            // The shape the composer replaces: a block glued ahead of the message with a bare blank line.
            Assert.DoesNotMatch(new Regex(@"(workspace|workspaceCtx|autoCtx)\s*\+\s*""\\n\\n""\s*\+"), code);
        }
    }

    [Fact]
    public void ThePlanPrompt_AllowsASingleStep()
    {
        Assert.Contains("Include 1–6 steps — only as many as the request needs.", ModelPrompts.AgentPlanPrompt,
                        StringComparison.Ordinal);
        Assert.DoesNotContain("2–6", ModelPrompts.AgentPlanPrompt, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
