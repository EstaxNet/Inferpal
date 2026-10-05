using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A VS Code turn reads in the order it happened: the run line, then the approval cards and the answer as they
/// arrived, then the result bar — the order Visual Studio shows.
/// </summary>
/// <remarks>
/// The turn's answer area (<c>turn-body</c>) is created with the turn, before any card. A card appended to the
/// turn's section therefore lands BELOW that area, and the final answer — written after every approval — shows
/// ABOVE the cards it depends on: the view scrolled to the bottom ends on the cards and the result bar, the answer
/// out of sight. ⚠ These rules read the source: the extension has no TypeScript test runner.
/// </remarks>
public class WebviewTurnOrderTests
{
    [Fact]
    public void AnApprovalCard_JoinsTheAnswerFlow_SoTheAnswerThatFollowsItReadsBelowIt()
    {
        var webview = WebviewRebuildTests.TsCode("webview/main.ts");

        // Witness: the turn holds its answer area from the start, and the result bar closes the section after it.
        Assert.Contains("body.className = 'turn-body'", WebviewRebuildTests.Body(webview, "function newTurn("),
                        StringComparison.Ordinal);
        Assert.Contains("target.el.appendChild(bar)", WebviewRebuildTests.Body(webview, "function renderRunSummary("),
                        StringComparison.Ordinal);
        // Same flow as the step-by-step banner, which already lands in the answer area.
        Assert.Contains("ensureTurn().body.appendChild(pause)", webview, StringComparison.Ordinal);

        var card = WebviewRebuildTests.Body(webview, "function addApprovalCard(");
        Assert.True(card.Contains("target.body.appendChild(el)", StringComparison.Ordinal),
            "The approval card is not put in the answer flow: the final answer then shows above the cards it "
            + "depends on, and the turn ends on the cards with its answer out of view.");
        Assert.DoesNotContain("target.el.appendChild(el)", card, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ A view rebuilt DURING a turn — a question renamed with the files it carries (nameAttachmentsInQuestion), a
    /// panel restored — closed every turn: the agent's plan, the status and the header of the answer being written then
    /// had nowhere to go, so the plan card never appeared and the next token opened a turn with no model name. Seen in
    /// image (render-vscode-chat.ps1, scene <c>rehydrate</c>) before and after.
    /// </summary>
    [Fact]
    public void AViewRebuiltDuringATurn_KeepsTheRunningTurnOpen()
    {
        var webview = WebviewRebuildTests.TsCode("webview/main.ts");

        // Witness: the rebuild closes the turns it draws, and the plan is drawn only into an open one.
        Assert.Contains("const target = turn?.plan", WebviewRebuildTests.Body(webview, "function renderPlan("), StringComparison.Ordinal);

        var rebuild = WebviewRebuildTests.Body(webview, "function renderTranscript(");
        Assert.Contains("if (running)", rebuild, StringComparison.Ordinal);
        Assert.Contains("turn ?? newTurn(currentModel)", rebuild, StringComparison.Ordinal);
        Assert.True(webview.Contains("renderTranscript(msg.transcript ?? [], msg.busy === true)", StringComparison.Ordinal),
            "The hydrate message does not say the turn is running: a rebuild during a turn drops its plan and its header.");
    }
}
