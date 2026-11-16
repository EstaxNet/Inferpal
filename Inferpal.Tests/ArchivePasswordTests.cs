using System.IO;
using System.Text;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A password-protected zip, read like a protected PDF: the person types the password on a card, the agent gets the
/// entry's text. Fixtures made by other tools — Windows' bsdtar (AES-256, ZipCrypto) and 7-Zip (AES-128, ZipCrypto
/// stored), password <c>pw123</c> and <c>K3y!</c> — so the decryption is never checked only against its own encryption.
/// </summary>
public sealed class ArchivePasswordTests
{
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "archives", name);
    }

    [Theory]
    [InlineData("aes.zip", "pw123")]        // bsdtar, AES-256, deflate
    [InlineData("zc.zip", "pw123")]         // bsdtar, ZipCrypto, deflate
    [InlineData("aes128.zip", "K3y!")]      // 7-Zip, AES-128, deflate
    [InlineData("zcstored.zip", "K3y!")]    // 7-Zip, ZipCrypto, stored
    public void TheRightPassword_OpensTheEntry(string archive, string password)
    {
        var path = Fixture(archive);
        Assert.True(ZipCrypt.IsEncrypted(path, "app.log"));

        var (bytes, outcome) = ZipCrypt.Read(path, "app.log", password, default);

        Assert.Equal(ZipCryptOutcome.Opened, outcome);
        Assert.Equal("secret log line\nERROR at Billing.cs:42\n", Encoding.UTF8.GetString(bytes!));
    }

    [Theory]
    [InlineData("aes.zip")]
    [InlineData("zc.zip")]
    [InlineData("aes128.zip")]
    [InlineData("zcstored.zip")]
    public void AWrongPassword_IsSaidWrong_NeverDamaged(string archive)
    {
        var path = Fixture(archive);
        foreach (var wrong in new[] { "nope", "pw124", "k3y!", "x" })
            Assert.Equal(ZipCryptOutcome.WrongPassword, ZipCrypt.Read(path, "app.log", wrong, default).Outcome);
    }

    // ── Through read_file, on the PDF's card ─────────────────────────────────────────────────────────────────────

    private sealed class CardDrawer(params string?[] typed) : Inferpal.Services.Execution.ApprovalServiceBase(new Inferpal.Config.InferpalConfig(), () => null)
    {
        private readonly Queue<string?> _typed = new(typed);
        public List<Inferpal.Services.Presentation.InputCardModel> Shown { get; } = [];
        public List<Inferpal.Services.Presentation.InputReceipt> Receipts { get; } = [];
        public bool Draws { get; init; } = true;
        public override bool CanAskForInput => Draws;
        protected override Task<Inferpal.Services.Execution.ApprovalDecision> PromptUserAsync(string message, Inferpal.Services.CodeActions.DiffInfo? diff, CancellationToken ct) =>
            Task.FromResult(Inferpal.Services.Execution.ApprovalDecision.Deny);
        protected override Task<Inferpal.Services.Presentation.InputReply?> PromptInputAsync(Inferpal.Services.Presentation.InputCardModel card, CancellationToken ct)
        {
            Shown.Add(card);
            if (_typed.Count == 0) return Task.FromResult<Inferpal.Services.Presentation.InputReply?>(null);
            var t = _typed.Dequeue();
            return Task.FromResult<Inferpal.Services.Presentation.InputReply?>(t is null
                ? new(Inferpal.Services.Mcp.McpInputAction.Cancel, new Dictionary<string, Inferpal.Services.Mcp.McpInputValue>())
                : new(Inferpal.Services.Mcp.McpInputAction.Accept, new Dictionary<string, Inferpal.Services.Mcp.McpInputValue>
                      { [Inferpal.Services.Presentation.InputCard.PasswordKey] = new(Text: t) }));
        }
        protected override void ShowInputReceipt(Inferpal.Services.Presentation.InputReceipt receipt) => Receipts.Add(receipt);
    }

    private static (string Root, string Archive) Copy(string fixture)
    {
        var root = Directory.CreateTempSubdirectory("inferpal-archivepw-").FullName;
        var archive = Path.Combine(root, "backup.zip");
        File.Copy(Fixture(fixture), archive);
        return (root, archive);
    }

    private static Task<string> Read(string root, CardDrawer approval) =>
        new Inferpal.Services.Tools.ReadFileTool(() => root, approval: approval)
            .ExecuteAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = "backup.zip!/app.log" }), default);

    [Fact]
    public async Task TheCard_AsksForTheArchive_AndTheModelGetsTheText_NotThePassword()
    {
        var (root, _) = Copy("aes.zip");
        try
        {
            var approval = new CardDrawer("pw123");

            var said = await Read(root, approval);

            Assert.Contains("ERROR at Billing.cs:42", said);
            Assert.DoesNotContain("pw123", said);
            var card = Assert.Single(approval.Shown);
            Assert.Contains("backup.zip!/app.log", card.Title);
            Assert.Equal(Inferpal.Localization.Strings.ArchivePasswordMessage, card.Message);
            Assert.DoesNotContain(approval.Receipts, r => r.Saved.Contains("pw123"));

            var again = await Read(root, new CardDrawer());   // kept for this archive: no second card
            Assert.Contains("ERROR at Billing.cs:42", again);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ThreeWrongPasswords_LeaveTheArchiveClosed_AndSaySo()
    {
        var (root, _) = Copy("zc.zip");
        try
        {
            var approval = new CardDrawer("a", "b", "c");

            var said = await Read(root, approval);

            Assert.Contains("stays closed", said);
            Assert.Equal(3, approval.Shown.Count);
            Assert.Contains("1", approval.Shown[2].Fields[0].Error);   // one try left, said on the third showing
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WhereNoCardCanBeDrawn_TheArchiveStaysClosed_AndIsSaidProtected()
    {
        var (root, _) = Copy("aes128.zip");
        try
        {
            var said = await Read(root, new CardDrawer { Draws = false });
            Assert.Contains("protected by a password", said);
            Assert.DoesNotContain("ERROR", said);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ListingAProtectedArchive_AsksForNothing()
    {
        var (root, _) = Copy("aes.zip");
        try
        {
            var listed = await new Inferpal.Services.Tools.ListFilesTool(() => root)
                .ExecuteAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = "backup.zip" }), default);
            Assert.Contains("backup.zip!/app.log", listed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AnEntryTooLargeOnceRead_IsSaidSo_WithoutACard()
    {
        var (root, _) = Copy("big.zip");   // 7-Zip, ZipCrypto, 9.5 MB of text packed in 11 KB, password pw123
        try
        {
            var approval = new CardDrawer("pw123");
            var said = await new Inferpal.Services.Tools.ReadFileTool(() => root, approval: approval)
                .ExecuteAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = "backup.zip!/big.log" }), default);
            Assert.Contains("larger than", said);
            Assert.Empty(approval.Shown);   // no card: no password would make it readable
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TwoEntriesReadInOneBatch_DrawOneCard()
    {
        var root = Directory.CreateTempSubdirectory("inferpal-archivepw-").FullName;
        try
        {
            File.Copy(Fixture("aes.zip"), Path.Combine(root, "backup.zip"));
            var approval = new CardDrawer("pw123");
            var tool = new Inferpal.Services.Tools.ReadFileTool(() => root, approval: approval);
            var args = System.Text.Json.JsonSerializer.SerializeToElement(new { path = "backup.zip!/app.log" });

            var both = await Task.WhenAll(tool.ExecuteAsync(args, default), tool.ExecuteAsync(args, default));

            Assert.All(both, said => Assert.Contains("ERROR at Billing.cs:42", said));
            Assert.Single(approval.Shown);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AnEntryNotThere_IsNotFound() =>
        Assert.Equal(ZipCryptOutcome.NotFound, ZipCrypt.Read(Fixture("aes.zip"), "other.txt", "pw123", default).Outcome);
}
