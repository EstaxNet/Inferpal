using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inferpal.Localization;
using Inferpal.Services.Execution;
using Inferpal.Services.Mcp;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The form an MCP server asks the person to fill (<c>elicitation/create</c>, form mode, revision 2026-07-28): read from
/// the schema the specification restricts, checked before it is sent, drawn as the same card in both editors, and the
/// line it leaves in the conversation.
/// </summary>
public class McpInputFormTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static McpInputForm Form(string schema, string message = "Which repository?")
    {
        var form = McpInputForm.Read("github", "create_release",
            El($$"""{"mode":"form","message":{{JsonSerializer.Serialize(message)}},"requestedSchema":{{schema}}}"""), out var refusal);
        Assert.True(form is not null, refusal);
        return form!;
    }

    private static IReadOnlyDictionary<string, McpInputValue> Values(params (string Key, McpInputValue Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    private static McpInputValue Text(string text) => new(Text: text);

    /// <summary>A form of one field, <paramref name="key"/>, of schema <paramref name="field"/>.</summary>
    private static string FieldSchema(string field, string key = "f") =>
        """{"type":"object","properties":{""" + JsonSerializer.Serialize(key) + ":" + field + "}}";

    // ── Reading the schema ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryKindOfFieldTheSpecificationAllows_IsRead_WithItsLabelBoundsAndDefault()
    {
        var form = Form("""
            {
              "type": "object",
              "properties": {
                "repo":    { "type": "string", "title": "Repository", "description": "owner/name", "minLength": 3, "maxLength": 50 },
                "contact": { "type": "string", "format": "email" },
                "days":    { "type": "integer", "minimum": 1, "maximum": 30, "default": 7 },
                "ratio":   { "type": "number", "maximum": 1.5 },
                "draft":   { "type": "boolean", "default": true },
                "bump":    { "type": "string", "enum": ["patch", "minor", "major"], "default": "minor" },
                "color":   { "type": "string", "oneOf": [{ "const": "#f00", "title": "Red" }, { "const": "#0f0", "title": "Green" }] },
                "legacy":  { "type": "string", "enum": ["a", "b"], "enumNames": ["Alpha", "Beta"] },
                "targets": { "type": "array", "minItems": 1, "items": { "type": "string", "enum": ["win", "linux", "mac"] }, "default": ["win", "nope"] },
                "titled":  { "type": "array", "items": { "anyOf": [{ "const": "x", "title": "Ex" }] } }
              },
              "required": ["repo", "bump", "missing"]
            }
            """);

        var f = form.Fields.ToDictionary(x => x.Key);
        Assert.Equal(["repo", "contact", "days", "ratio", "draft", "bump", "color", "legacy", "targets", "titled"], form.Fields.Select(x => x.Key));
        Assert.Equal(("Repository", McpInputKind.Text, true, 3, 50), (f["repo"].Label, f["repo"].Kind, f["repo"].Required, f["repo"].MinLength, f["repo"].MaxLength));
        Assert.Equal(("contact", McpTextFormat.Email, false), (f["contact"].Label, f["contact"].Format, f["contact"].Required));
        Assert.Equal((McpInputKind.Integer, 1.0, 30.0), (f["days"].Kind, f["days"].Minimum, f["days"].Maximum));
        Assert.NotNull(f["days"].Default?.Text);
        Assert.Equal((McpInputKind.Number, (double?)null, 1.5), (f["ratio"].Kind, f["ratio"].Minimum, f["ratio"].Maximum));
        Assert.Equal(true, f["draft"].Default?.Flag);
        Assert.Equal(McpInputKind.Choice, f["bump"].Kind);
        Assert.Equal("minor", f["bump"].Default?.Text);
        Assert.Equal(["Red", "Green"], f["color"].Choices.Select(o => o.Label));
        Assert.Equal(["#f00", "#0f0"], f["color"].Choices.Select(o => o.Value));
        Assert.Equal(["Alpha", "Beta"], f["legacy"].Choices.Select(o => o.Label));
        Assert.Equal((McpInputKind.Choices, 1), (f["targets"].Kind, f["targets"].MinItems));
        // A default that names no choice is left out, never sent as one the person picked.
        Assert.Equal(["win"], f["targets"].Default?.Picks);
        Assert.Equal(["Ex"], f["titled"].Choices.Select(o => o.Label));
        Assert.Equal("Which repository?", form.Message);
    }

    [Theory]
    [InlineData("""{"mode":"url","url":"https://x.example/key","message":"Your key"}""", "url mode")]
    [InlineData("""{"mode":"wizard","message":"m","requestedSchema":{"type":"object","properties":{}}}""", "'wizard'")]
    [InlineData("""{"requestedSchema":{"type":"object","properties":{}}}""", "no message")]
    [InlineData("""{"message":"m","requestedSchema":{"type":"object","properties":{"nested":{"type":"object"}}}}""", "'nested'")]
    [InlineData("""{"message":"m","requestedSchema":{"type":"object","properties":{"when":{"type":"string","format":"time"}}}}""", "format 'time'")]
    [InlineData("""{"message":"m","requestedSchema":{"type":"object","properties":{"list":{"type":"array","items":{"type":"number"}}}}}""", "'list'")]
    [InlineData("""{"message":"m","requestedSchema":{"type":"array"}}""", "not an object schema")]
    public void AFormThisClientCannotShowAsItIs_IsRefused_WithTheReason(string @params, string said)
    {
        Assert.Null(McpInputForm.Read("s", "t", El(@params), out var refusal));
        Assert.Contains(said, refusal);
    }

    [Fact]
    public void AFormWithoutFields_IsAQuestionToConfirm_AndSendsNothingButTheAnswer()
    {
        // A server may only ask "go on?": the message, Send, Decline, Cancel.
        var form = Form("""{"type":"object","properties":{}}""");
        Assert.Empty(form.Fields);
        var check = form.Check(Values());
        Assert.True(check.Passed);
        Assert.Equal("{}", check.Content!.ToJsonString());
    }

    // ── Checking the values ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WhatPassesTheSchema_IsSent_InTheTypesTheSchemaAsks_AndInItsOrder()
    {
        var form = Form("""
            {"type":"object","properties":{
              "repo":{"type":"string"},"days":{"type":"integer"},"ratio":{"type":"number"},"draft":{"type":"boolean"},
              "bump":{"type":"string","enum":["patch","minor"]},"targets":{"type":"array","items":{"type":"string","enum":["win","linux","mac"]}},
              "note":{"type":"string"}}}
            """);
        McpInputCheck check;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            check = form.Check(Values(
                ("repo", Text("  dams/inferpal ")), ("days", Text("7")), ("ratio", Text("7,5")), ("draft", new McpInputValue(Flag: false)),
                ("bump", Text("minor")), ("targets", new McpInputValue(Picks: ["mac", "win", "mac"])), ("note", Text("   "))));
        }
        finally { CultureInfo.CurrentCulture = culture; }

        Assert.True(check.Passed, string.Join("; ", check.Errors));
        // Free text goes as typed; a comma is the decimal separator a French keyboard types; an unticked box is "no";
        // the choices go in the schema's order, once; an optional field left empty is not sent at all.
        Assert.Equal("""{"repo":"  dams/inferpal ","days":7,"ratio":7.5,"draft":false,"bump":"minor","targets":["win","mac"]}""",
                     check.Content!.ToJsonString());
    }

    [Theory]
    [InlineData("""{"type":"string","format":"email"}""", "dams@", nameof(Strings.InputErrEmail))]
    [InlineData("""{"type":"string","format":"email"}""", "Dams <dams@example.com>", nameof(Strings.InputErrEmail))]
    [InlineData("""{"type":"string","format":"uri"}""", @"C:\temp\x", nameof(Strings.InputErrUri))]
    [InlineData("""{"type":"string","format":"uri"}""", "example.com", nameof(Strings.InputErrUri))]
    [InlineData("""{"type":"string","format":"date"}""", "09/11/2026", nameof(Strings.InputErrDate))]
    [InlineData("""{"type":"string","format":"date"}""", "2026-02-30", nameof(Strings.InputErrDate))]
    [InlineData("""{"type":"string","format":"date-time"}""", "2026-11-09T14:30:00", nameof(Strings.InputErrDateTime))]
    [InlineData("""{"type":"string","format":"date-time"}""", "2026-11-09T14:30Z", nameof(Strings.InputErrDateTime))]
    [InlineData("""{"type":"integer"}""", "7.5", nameof(Strings.InputWhole))]
    [InlineData("""{"type":"number"}""", "seven", nameof(Strings.InputNumber))]
    [InlineData("""{"type":"string","enum":["a","b"]}""", "c", nameof(Strings.InputChooseOne))]
    public void AValueThatBreaksTheSchema_IsNeverSent_AndTheFieldSaysWhatToFix(string schema, string typed, string expected)
    {
        var form = Form(FieldSchema(schema));
        var check = form.Check(Values(("f", Text(typed))));
        Assert.False(check.Passed);
        Assert.Null(check.Content);
        Assert.Equal(Strings.ByName(expected), check.Errors["f"]);
    }

    [Theory]
    [InlineData("""{"type":"string","format":"email"}""", "dams@example.com")]
    [InlineData("""{"type":"string","format":"uri"}""", "https://example.com/a?b=c")]
    [InlineData("""{"type":"string","format":"uri"}""", "mailto:dams@example.com")]
    [InlineData("""{"type":"string","format":"date"}""", "2026-11-09")]
    [InlineData("""{"type":"string","format":"date-time"}""", "2026-11-09T14:30:00Z")]
    [InlineData("""{"type":"string","format":"date-time"}""", "2026-11-09T14:30:00.250+02:00")]
    [InlineData("""{"type":"integer","minimum":1,"maximum":30}""", "30")]
    public void AValueOfItsFormat_Passes(string schema, string typed) =>
        Assert.True(Form(FieldSchema(schema)).Check(Values(("f", Text(typed)))).Passed);

    [Fact]
    public void BoundsLengthsAndCounts_AreSaid_TheSameUnderTheFieldAndAsTheError()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var form = Form("""
                {"type":"object","properties":{
                  "days":{"type":"integer","minimum":1,"maximum":30},
                  "title":{"type":"string","maxLength":5},
                  "targets":{"type":"array","minItems":1,"maxItems":2,"items":{"type":"string","enum":["a","b","c"]}}},
                 "required":["days"]}
                """);
            var check = form.Check(Values(("days", Text("45")), ("title", Text("too long")), ("targets", new McpInputValue(Picks: ["a", "b", "c"]))));

            Assert.Equal(Strings.InputRangeWhole("1", "30"), check.Errors["days"]);
            Assert.Equal(Strings.InputLengthAtMost(5), check.Errors["title"]);
            Assert.Equal(Strings.InputChooseBetween(1, 2), check.Errors["targets"]);
            Assert.Equal(Strings.InputRangeWhole("1", "30"), McpInputForm.Hint(form.Fields[0]));

            // Required, and left empty.
            Assert.Equal(Strings.InputErrRequired, form.Check(Values(("days", Text(" ")))).Errors["days"]);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData("en-US", """{"type":"number"}""", "1,000", null)]
    [InlineData("fr-FR", """{"type":"integer"}""", "1,000", null)]
    [InlineData("fr-FR", """{"type":"number"}""", "1,5", "1.5")]
    [InlineData("sv-SE", """{"type":"integer"}""", "\u22121", "-1")]
    [InlineData("fr-FR", """{"type":"integer"}""", "1\u202F000", "1000")]
    [InlineData("en-US", """{"type":"number"}""", "1.5", "1.5")]
    public void ANumberIsReadAsItsCultureWritesIt_AndAThousandIsNeverOne(string culture, string schema, string typed, string? sent)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var check = Form(FieldSchema(schema)).Check(Values(("f", Text(typed))));
            Assert.Equal(sent, check.Passed ? check.Content!["f"]!.ToJsonString() : null);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ADefaultNumber_IsFilledInAsEveryCultureReadsIt()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            var form = Form(FieldSchema("""{"type":"number","default":-1.5}"""));
            Assert.Equal("-1.5", form.Initial()["f"].Text);
            Assert.True(form.Check(form.Initial()).Passed);                    // sent as it came, untouched
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void AnOptionalSetOfChoicesWithAMinimum_CanBeLeftOut()
    {
        var form = Form(FieldSchema("""{"type":"array","minItems":2,"items":{"type":"string","enum":["a","b"]}}"""));
        var check = form.Check(Values());
        Assert.True(check.Passed);
        Assert.Equal("{}", check.Content!.ToJsonString());
        Assert.Equal(Strings.InputChooseAtLeast(2), form.Check(Values(("f", new McpInputValue(Picks: ["a"])))).Errors["f"]);
    }

    [Fact]
    public void ARequiredSetOfChoices_LeftEmpty_IsToFix_UnlessTheServerTakesNone()
    {
        var form = Form("""
            {"type":"object","properties":{
              "targets":{"type":"array","items":{"type":"string","enum":["a","b"]}},
              "optional":{"type":"array","minItems":0,"items":{"type":"string","enum":["a","b"]}}},
             "required":["targets","optional"]}
            """);
        var check = form.Check(Values());
        Assert.Equal(Strings.InputChooseAtLeast(1), check.Errors["targets"]);
        Assert.False(check.Errors.ContainsKey("optional"));
    }

    [Fact]
    public void AWholeNumberPastTheLongRange_IsSentAsTheNumberTyped_NeverWrappedAround()
    {
        var form = Form(FieldSchema("""{"type":"integer"}"""));
        Assert.Equal("""{"f":9223372036854775807}""", form.Check(Values(("f", Text("9223372036854775807")))).Content!.ToJsonString());
        var past = form.Check(Values(("f", Text("9223372036854775808")))).Content!["f"]!;
        Assert.True(past.GetValue<double>() > 0);
    }

    [Fact]
    public void AValueThatReadsLikeASecret_IsSent_ButNeverKeptInTheReceipt()
    {
        var form = Form("""{"type":"object","properties":{"apiKey":{"type":"string"},"repo":{"type":"string"}}}""");
        var check = form.Check(Values(("apiKey", Text("sk-123")), ("repo", Text("dams/inferpal"))));
        Assert.Equal("sk-123", (string?)check.Content!["apiKey"]);                       // the server gets what was typed
        var receipt = InputCard.Receipt(form, "c1", McpInputAction.Accept, check.Content);
        Assert.DoesNotContain("sk-123", receipt.Saved);
        Assert.Contains("apiKey: " + InputCard.HiddenValue, receipt.Body);
        Assert.Contains("repo: dams/inferpal", receipt.Body);
    }

    [Theory]
    [InlineData("password", true)]
    [InlineData("apiKey", true)]
    [InlineData("github_token", true)]
    [InlineData("AccessKey", true)]
    [InlineData("pinnedCount", false)]
    [InlineData("passport", false)]
    [InlineData("tokenizer", false)]
    [InlineData("repository", false)]
    public void AFieldThatReadsLikeASecret_IsWarnedAbout_ByItsWordsNeverASubstring(string key, bool warns)
    {
        var form = Form(FieldSchema("""{"type":"string"}""", key));
        Assert.Equal(warns, McpInputForm.LooksSecret(form.Fields[0]));
        var card = InputCard.Build(form, "c1", form.Initial(), new Dictionary<string, string>());
        Assert.Equal(warns, card.Fields[0].Warns);
        Assert.Equal(warns, card.Fields[0].Hint.Contains(Strings.InputSecretWarning, StringComparison.Ordinal));
    }

    // ── The answer the server gets ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheAnswer_IsTheSpecificationsResult_AndOnlyASentFormCarriesContent()
    {
        var content = new JsonObject { ["repo"] = "dams/inferpal" };
        Assert.Equal("""{"action":"accept","content":{"repo":"dams/inferpal"}}""", new McpInputAnswer(McpInputAction.Accept, content).Result().ToJsonString());
        Assert.Equal("""{"action":"decline"}""", new McpInputAnswer(McpInputAction.Decline).Result().ToJsonString());
        Assert.Equal("""{"action":"cancel"}""", new McpInputAnswer(McpInputAction.Cancel).Result().ToJsonString());
        // A note that took the card away, and a card that could not be drawn, are "dismissed without a choice".
        Assert.Equal("""{"action":"cancel"}""", new McpInputAnswer(McpInputAction.SetAside).Result().ToJsonString());
        Assert.Equal("""{"action":"cancel"}""", new McpInputAnswer(McpInputAction.NotShown).Result().ToJsonString());
        Assert.Contains("not to you", new McpInputAnswer(McpInputAction.Accept, content).NoteForModel("github"));
        Assert.Contains("declined", new McpInputAnswer(McpInputAction.Decline).NoteForModel("github"));
    }

    // ── The card ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCard_NamesTheServerAndTheCall_SaysTheServersWords_AndKeepsWhatToFix()
    {
        var form = Form("""
            {"type":"object","properties":{
              "repo":{"type":"string"},
              "bump":{"type":"string","enum":["patch","minor","major"]},
              "branch":{"type":"string","enum":["main","release/1.9","develop","feature/a-very-long-branch-name"]}},
             "required":["repo"]}
            """);
        var card = InputCard.Build(form, "c1", Values(("repo", Text("x"))), new Dictionary<string, string> { ["repo"] = "fix it" });

        Assert.Equal(Strings.InputCardTitle("github"), card.Title);
        Assert.Equal(Strings.InputCardSubtitle("create_release"), card.Subtitle);
        Assert.Equal("Which repository?", card.Message);
        Assert.Equal(Strings.InputFixCount(1), card.ErrorSummary);
        Assert.Equal(Strings.InputCardRequired, card.RequiredNote);
        Assert.Equal(("x", "fix it"), (card.Fields[0].Text, card.Fields[0].Error));
        // Three short choices are buttons; a long one makes the field a list.
        Assert.True(card.Fields[1].Buttons);
        Assert.False(card.Fields[2].Buttons);
        Assert.Equal(Strings.InputDeclineTip("github"), card.DeclineTooltip);
        Assert.Equal(Strings.InputCancelTip("github"), card.CancelTooltip);
        // A first showing has nothing to fix.
        Assert.Empty(InputCard.Build(form, "c1", form.Initial(), new Dictionary<string, string>()).ErrorSummary);
    }

    [Fact]
    public void TheReceipt_SaysWhatThePersonDid_AndWhatTheySent_AsTheyChoseIt_AndComesBackFromTheSavedConversation()
    {
        var form = Form("""
            {"type":"object","properties":{
              "repo":{"type":"string","title":"Repository"},"draft":{"type":"boolean","title":"Draft"},
              "color":{"type":"string","oneOf":[{"const":"#f00","title":"Red"}]},
              "targets":{"type":"array","title":"Targets","items":{"anyOf":[{"const":"w","title":"Windows"},{"const":"l","title":"Linux"}]}}}}
            """);
        var check = form.Check(Values(("repo", Text("dams/inferpal")), ("draft", new McpInputValue(Flag: true)),
                                      ("color", Text("#f00")), ("targets", new McpInputValue(Picks: ["l", "w"]))));
        var receipt = InputCard.Receipt(form, "c1", McpInputAction.Accept, check.Content);

        Assert.Equal(Strings.InputReceiptSent("github"), receipt.Title);
        Assert.Equal(Strings.InputReceiptSentDetail(4), receipt.Detail);
        Assert.Contains("Repository: dams/inferpal\n", receipt.Body);
        Assert.Contains("Draft: " + Strings.InputYes + "\n", receipt.Body);
        Assert.Contains("color: Red\n", receipt.Body);
        Assert.Contains("Targets: Windows, Linux\n", receipt.Body);
        Assert.EndsWith(Strings.InputReceiptSentNote("github"), receipt.Body);

        var back = InputReceipt.FromSaved(receipt.Saved);
        Assert.Equal((receipt.Title, receipt.Detail, receipt.Body), (back.Title, back.Detail, back.Body));

        var declined = InputCard.Receipt(form, "c1", McpInputAction.Decline, null);
        Assert.Equal((Strings.InputReceiptDeclined("github"), Strings.InputReceiptNothingSent, ""), (declined.Title, declined.Detail, declined.Body));
        Assert.Equal(declined.Title + "\n" + declined.Detail, declined.Saved);
        Assert.Equal(Strings.InputReceiptCancelled("github"), InputCard.Receipt(form, "c1", McpInputAction.Cancel, null).Title);
        Assert.Equal(Strings.InputReceiptSetAside("github"), InputCard.Receipt(form, "c1", McpInputAction.SetAside, null).Title);
        Assert.Equal(Strings.InputReceiptStopped("github"), InputCard.Stopped(form, "c1").Title);
    }

    // ── The loop: shown again until the values pass, or the person declines ──────────────────────────────────────

    private sealed class CardDrawer(params InputReply?[] replies) : ApprovalServiceBase(new Inferpal.Config.InferpalConfig(), () => null)
    {
        private readonly Queue<InputReply?> _replies = new(replies);
        public List<InputCardModel> Shown { get; } = [];
        public List<InputReceipt> Receipts { get; } = [];
        public bool Draws { get; init; } = true;
        public bool StopOnShow { get; init; }

        public override bool CanAskForInput => Draws;

        protected override Task<ApprovalDecision> PromptUserAsync(string message, Services.CodeActions.DiffInfo? diff, CancellationToken ct) =>
            Task.FromResult(ApprovalDecision.Deny);

        protected override Task<InputReply?> PromptInputAsync(InputCardModel card, CancellationToken ct)
        {
            Shown.Add(card);
            if (StopOnShow) throw new OperationCanceledException();
            return Task.FromResult(_replies.Dequeue());
        }

        protected override void ShowInputReceipt(InputReceipt receipt) => Receipts.Add(receipt);
    }

    private static McpInputForm DaysForm() =>
        Form("""{"type":"object","properties":{"days":{"type":"integer","minimum":1,"maximum":30,"default":7}},"required":["days"]}""");

    [Fact]
    public async Task ACardWhoseValuesDoNotPass_IsShownAgain_UnderItsId_WithTheValuesAsLeft_ThenSent()
    {
        var drawer = new CardDrawer(
            new InputReply(McpInputAction.Accept, Values(("days", Text("45")))),
            new InputReply(McpInputAction.Accept, Values(("days", Text("12")))));

        var answer = await drawer.AskForInputAsync(DaysForm(), CancellationToken.None);

        Assert.Equal(McpInputAction.Accept, answer.Action);
        Assert.Equal("""{"days":12}""", answer.Content!.ToJsonString());
        Assert.Equal(2, drawer.Shown.Count);
        Assert.Equal(drawer.Shown[0].Id, drawer.Shown[1].Id);
        Assert.Equal("", drawer.Shown[0].Fields[0].Error);
        Assert.NotEmpty(drawer.Shown[0].Fields[0].Text);                // the default, shown first
        Assert.Equal("45", drawer.Shown[1].Fields[0].Text);              // never reset to the default
        Assert.NotEmpty(drawer.Shown[1].Fields[0].Error);
        // What the field takes is said once: by its hint, or by its error when it has one.
        Assert.Equal(drawer.Shown[1].Fields[0].Error, drawer.Shown[0].Fields[0].Hint);
        Assert.Equal("", drawer.Shown[1].Fields[0].Hint);
        // One line, once the answer went: never one per showing.
        Assert.Single(drawer.Receipts);
        Assert.Equal(drawer.Shown[0].Id, drawer.Receipts[0].CardId);
    }

    [Fact]
    public async Task DecliningOrCancelling_SendsNoValues_AndLeavesItsLine()
    {
        var drawer = new CardDrawer(new InputReply(McpInputAction.Decline, Values(("days", Text("45")))));
        var answer = await drawer.AskForInputAsync(DaysForm(), CancellationToken.None);
        Assert.Equal(McpInputAction.Decline, answer.Action);
        Assert.Null(answer.Content);
        Assert.Equal(Strings.InputReceiptDeclined("github"), Assert.Single(drawer.Receipts).Title);
    }

    [Fact]
    public async Task ACardThatCannotBeDrawn_IsNotShown_AndLeavesNoLine()
    {
        var drawer = new CardDrawer((InputReply?)null);
        Assert.Equal(McpInputAction.NotShown, (await drawer.AskForInputAsync(DaysForm(), CancellationToken.None)).Action);
        Assert.Empty(drawer.Receipts);
    }

    [Fact]
    public async Task StoppedWhileTheCardWaits_TheRunStops_AndTheCardSaysItWentUnanswered()
    {
        var drawer = new CardDrawer { StopOnShow = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drawer.AskForInputAsync(DaysForm(), CancellationToken.None));
        Assert.Equal(Strings.InputReceiptStopped("github"), Assert.Single(drawer.Receipts).Title);
    }

    [Fact]
    public void APipelineThatDrawsNoCard_NeverLetsAServerAsk()
    {
        // The default: a /task recorder, a client without the card. And the /tdd guard asks what it wraps.
        IApprovalService plain = new CardDrawer { Draws = false };
        Assert.False(plain.CanAskForInput);
        Assert.True(new Services.Commands.TestFileWriteGuard(new CardDrawer()).CanAskForInput);
        Assert.False(new Services.Commands.TestFileWriteGuard(plain).CanAskForInput);
    }
}
