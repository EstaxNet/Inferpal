using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Mcp;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A protected PDF the agent reads (pari 85, passwords): the person, on a card, types the password; the model gets the
/// text, never the password; three wrong tries leave the file closed; where no card can be drawn the file stays closed
/// and is said protected.
/// </summary>
public sealed class DocumentPasswordTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-pdf-password-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

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
            return Task.FromResult(_replies.Dequeue());
        }

        protected override void ShowInputReceipt(InputReceipt receipt) => Receipts.Add(receipt);
    }

    private static InputReply Typed(string password) =>
        new(McpInputAction.Accept, new Dictionary<string, McpInputValue> { [InputCard.PasswordKey] = new(Text: password) });

    private string Protected(string password)
    {
        var path = Path.Combine(_dir, $"contrat-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, DocumentTextTests.EncryptedPdf("late delivery costs half a percent a day", password, aes256: true));
        return path;
    }

    private Task<string> Read(string path, CardDrawer drawer) =>
        new ReadFileTool(() => _dir, approval: drawer)
            .ExecuteAsync(JsonDocument.Parse(JsonSerializer.Serialize(new { path })).RootElement, CancellationToken.None);

    [Fact]
    public async Task TheAgentsRead_AsksThePerson_AndTheModelGetsTheText_NeverThePassword()
    {
        var path = Protected("tr0ub4dor");
        var drawer = new CardDrawer(Typed("wrong-one"), Typed("tr0ub4dor"));

        var result = await Read(path, drawer);

        Assert.Contains("late delivery costs half a percent a day", result);
        Assert.DoesNotContain("tr0ub4dor", result);
        // Asked again under the same card after a wrong try, with what to fix; the box drawn empty every time.
        Assert.Equal(2, drawer.Shown.Count);
        Assert.Equal(drawer.Shown[0].Id, drawer.Shown[1].Id);
        Assert.Equal("password", drawer.Shown[0].Fields[0].InputType);
        Assert.All(drawer.Shown, c => Assert.Equal("", c.Fields[0].Text));
        Assert.Equal(Strings.DocPasswordWrong(Path.GetFileName(path), 2), drawer.Shown[1].Fields[0].Error);
        // One line kept in the conversation: opened, without the password.
        var receipt = Assert.Single(drawer.Receipts);
        Assert.Equal(Strings.DocPasswordReceiptOpened(Path.GetFileName(path)), receipt.Title);
        Assert.DoesNotContain("tr0ub4dor", receipt.Saved);
        // Kept for that file, in memory: the next read asks nothing.
        var again = new CardDrawer();
        Assert.Contains("half a percent", await Read(path, again));
        Assert.Empty(again.Shown);
    }

    [Fact]
    public async Task LeftClosed_OrThreeWrongTries_TheModelIsToldItStaysClosed()
    {
        var closed = new CardDrawer(new InputReply(McpInputAction.Cancel, new Dictionary<string, McpInputValue>()));
        var left = Protected("a");
        Assert.Contains("stays closed", await Read(left, closed));
        Assert.Equal(Strings.DocPasswordReceiptClosed(Path.GetFileName(left)), Assert.Single(closed.Receipts).Title);

        // An empty box is no try: four showings for three wrong passwords.
        var wrong = new CardDrawer(Typed(""), Typed("x"), Typed("y"), Typed("z"));
        var path = Protected("right");
        Assert.Contains("stays closed", await Read(path, wrong));
        Assert.Equal(4, wrong.Shown.Count);
        Assert.Equal(Strings.DocPasswordEmpty, wrong.Shown[1].Fields[0].Error);
        Assert.Equal(Strings.DocPasswordReceiptTooMany(Path.GetFileName(path)), Assert.Single(wrong.Receipts).Title);
    }

    /// <summary>
    /// Visual Studio's card: Remote UI binds no <c>PasswordBox.Password</c>, so the in-process relay copies what is typed —
    /// only for the box that carries its mark. A mark changed on one side alone leaves the card answering "type the
    /// password first" for ever (an <c>x:Uid</c> mark never reached the relay: Remote UI drops it).
    /// </summary>
    [Fact]
    public void TheCardsPasswordBox_CarriesTheMarkTheRelayReads()
    {
        var root = WebviewRebuildTests.RepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml"));
        var relay = File.ReadAllText(Path.Combine(root, "Inferpal.InProc", "GhostText", "PasswordBoxRelay.cs"));
        Assert.Contains("<PasswordBox AutomationProperties.AutomationId=\"InferpalPasswordBox\"", xaml);
        Assert.Contains("internal const string BoxUid = \"InferpalPasswordBox\";", relay);
        Assert.Contains("AutomationProperties.GetAutomationId(box) != BoxUid", relay);
    }

    [Fact]
    public async Task WhereNoCardCanBeDrawn_NothingIsAsked_AndTheFileIsSaidProtected()
    {
        var drawer = new CardDrawer { Draws = false };
        Assert.Contains("opens only with a password", await Read(Protected("p"), drawer));
        Assert.Empty(drawer.Shown);
    }
}
