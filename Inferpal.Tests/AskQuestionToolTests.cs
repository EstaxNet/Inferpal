using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Mcp;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>ask_question</c>: the agent asks the person on a card, with the answers it proposes, and reads what they answered or
/// that they let it decide. Offered only where a card can be drawn; only the person answers.
/// </summary>
public sealed class AskQuestionToolTests
{
    private sealed class CardDrawer(params InputReply?[] replies) : ApprovalServiceBase(new Inferpal.Config.InferpalConfig(), () => null)
    {
        private readonly Queue<InputReply?> _replies = new(replies);
        public List<InputCardModel> Shown { get; } = [];
        public List<InputReceipt> Receipts { get; } = [];
        public bool Draws { get; init; } = true;
        public override bool CanAskForInput => Draws;
        protected override Task<ApprovalDecision> PromptUserAsync(string message, Services.CodeActions.DiffInfo? diff, CancellationToken ct) =>
            Task.FromResult(ApprovalDecision.Deny);
        protected override Task<InputReply?> PromptInputAsync(InputCardModel card, CancellationToken ct)
        {
            Shown.Add(card);
            return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : null);
        }
        protected override void ShowInputReceipt(InputReceipt receipt) => Receipts.Add(receipt);
    }

    private static InputReply Answer(string? picked = null, string? own = null)
    {
        var values = new Dictionary<string, McpInputValue>();
        if (picked is not null) values[InputCard.AnswerKey] = new(Text: picked);
        if (own is not null) values[InputCard.OtherKey] = new(Text: own);
        return new(McpInputAction.Accept, values);
    }

    private static Task<string> Ask(CardDrawer approval, object args) =>
        new AskQuestionTool(approval).ExecuteAsync(JsonSerializer.SerializeToElement(args), default);

    [Fact]
    public async Task TheCard_ShowsTheQuestionAndTheProposedAnswers_AndTheModelReadsThePick()
    {
        var approval = new CardDrawer(Answer(picked: "SignupForm"));

        var said = await Ask(approval, new { question = "Which form?", options = new[] { "LoginForm", "SignupForm", "ProfileForm" } });

        Assert.Equal("The user answered: SignupForm", said);
        var card = Assert.Single(approval.Shown);
        Assert.Equal("Which form?", card.Message);
        Assert.Equal(Strings.AskCardTitle, card.Title);
        Assert.Equal(["LoginForm", "SignupForm", "ProfileForm"], card.Fields[0].Options.Select(o => o.Label));
        Assert.Equal(string.Empty, card.DeclineLabel);   // answer or let the agent decide: no third way
        Assert.Equal(Strings.AskCardSkip, card.CancelLabel);
        Assert.Equal(Strings.AskReceiptAnswered, Assert.Single(approval.Receipts).Title);
    }

    [Fact]
    public async Task TheirOwnWords_WinOverAProposal()
    {
        var said = await Ask(new CardDrawer(Answer(picked: "A", own: "Neither: the admin form")), new { question = "Which?", options = new[] { "A", "B" } });
        Assert.Equal("The user answered: Neither: the admin form", said);
    }

    [Fact]
    public async Task NothingPicked_ShowsTheCardAgain_WithWhatToDo()
    {
        var approval = new CardDrawer(Answer(), Answer(picked: "B"));

        var said = await Ask(approval, new { question = "Which?", options = new[] { "A", "B" } });

        Assert.Equal("The user answered: B", said);
        Assert.Equal(2, approval.Shown.Count);
        Assert.Equal(approval.Shown[0].Id, approval.Shown[1].Id);   // the same card, drawn again
        Assert.Equal(Strings.AskCardEmpty, approval.Shown[1].Fields[0].Error);
    }

    [Fact]
    public async Task LettingTheAgentDecide_TellsItToSayWhatItAssumed()
    {
        var approval = new CardDrawer(new InputReply(McpInputAction.Cancel, new Dictionary<string, McpInputValue>()));

        var said = await Ask(approval, new { question = "Which?" });

        Assert.Contains("let you decide", said);
        Assert.Contains("what you assumed", said);
        Assert.Equal(Strings.AskReceiptSkipped, Assert.Single(approval.Receipts).Title);
    }

    [Fact]
    public async Task WithoutProposals_TheCardIsAFreeAnswer_Required()
    {
        var approval = new CardDrawer(new InputReply(McpInputAction.Accept,
            new Dictionary<string, McpInputValue> { [InputCard.AnswerKey] = new(Text: "UTC") }));

        var said = await Ask(approval, new { question = "Which time zone?" });

        Assert.Equal("The user answered: UTC", said);
        var field = Assert.Single(approval.Shown[0].Fields);
        Assert.True(field.Required);
        Assert.Equal("text", field.Kind);
    }

    [Fact]
    public async Task NoCardDrawn_TheModelDecides_AndIsToldWhy()
    {
        var said = await Ask(new CardDrawer(), new { question = "Which?" });
        Assert.Contains("could not be shown", said);
    }

    [Fact]
    public void ItIsOffered_OnlyWhereACardCanBeDrawn()
    {
        Assert.True(new AskQuestionTool(new CardDrawer()).IsOffered);
        Assert.False(new AskQuestionTool(new CardDrawer { Draws = false }).IsOffered);
    }

    [Fact]
    public async Task ANoteTypedWhileTheCardWaits_IsNoLetDecide()
    {
        var approval = new CardDrawer(new InputReply(McpInputAction.SetAside, new Dictionary<string, McpInputValue>()));

        var said = await Ask(approval, new { question = "Which form?", options = new[] { "A", "B" } });

        Assert.Contains("wrote you a note", said);
        Assert.DoesNotContain("let you decide", said);
        Assert.Equal(Strings.AskReceiptNote, Assert.Single(approval.Receipts).Title);
    }

    private sealed class Stopping : ApprovalServiceBase
    {
        public Stopping() : base(new Inferpal.Config.InferpalConfig(), () => null) { }
        public List<InputReceipt> Receipts { get; } = [];
        public override bool CanAskForInput => true;
        protected override Task<ApprovalDecision> PromptUserAsync(string message, Services.CodeActions.DiffInfo? diff, CancellationToken ct) =>
            Task.FromResult(ApprovalDecision.Deny);
        protected override Task<InputReply?> PromptInputAsync(InputCardModel card, CancellationToken ct) =>
            throw new OperationCanceledException();
        protected override void ShowInputReceipt(InputReceipt receipt) => Receipts.Add(receipt);
    }

    [Fact]
    public async Task AStopWhileTheCardWaits_EndsTheRun_AndSaysStopped()
    {
        var approval = new Stopping();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AskQuestionTool(approval).ExecuteAsync(JsonSerializer.SerializeToElement(new { question = "Which?" }), default));
        Assert.Equal(Strings.AskReceiptStopped, Assert.Single(approval.Receipts).Title);
    }

    [Fact]
    public async Task WhereNoCardCanBeDrawn_NoCardIsDrawn_EvenForACallMadeAnyway()
    {
        var approval = new CardDrawer { Draws = false };
        var said = await Ask(approval, new { question = "Which?" });
        Assert.Contains("could not be shown", said);
        Assert.Empty(approval.Shown);
    }

    [Fact]
    public void ABackgroundTask_NeverGetsTheTool()
    {
        Assert.False(ToolCeilings.AllowedInBackgroundTask("ask_question"));
        Assert.True(ToolCeilings.AllowedInBackgroundTask("read_file"));   // witness
    }

    [Fact]
    public async Task MoreThanFourProposals_KeepsFour_AndSaysSo()
    {
        var approval = new CardDrawer(Answer(picked: "a"));

        var said = await Ask(approval, new { question = "Which?", options = new[] { "a", "b", "c", "d", "e" } });

        Assert.Equal(AskQuestionTool.MaxOptions, approval.Shown[0].Fields[0].Options.Count);
        Assert.StartsWith("(Only the first 4", said);
    }
}
