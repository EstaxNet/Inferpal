using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Execution;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The approval of a change to several files tells the human what the change touches: every file, counted in their
/// language — the approval is the boundary, and what it does not say is approved unread.
/// </summary>
/// <remarks>
/// ⚠ An <c>apply_edits</c> batch has every path as its subject, one per line, so that the rules match each. The card
/// read it as ONE path: titled "Change a file", it named the first file of a two-file batch. And the approval of
/// <c>rename_symbol</c> was written in English in all ten languages — its warning that a text-based rename takes
/// unrelated symbols too included — and did not say that the scan had missed files, which makes the rename partial.
/// </remarks>
[Collection(CultureSerialCollection.Name)]
public sealed class MultiFileApprovalCardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-multi-approval").FullName;

    public void Dispose()
    {
        Strings.ApplyLanguage(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* cleanup */ }
    }

    /// <summary>Draws the card the way both editors do, from the prompt the approval funnel builds, and refuses.</summary>
    private sealed class CardSpy(Func<string?> root) : ApprovalServiceBase(new InferpalConfig(), root)
    {
        public ApprovalCardModel? Card;
        public string Details = string.Empty;

        protected override Task<ApprovalDecision> PromptUserAsync(ApprovalPrompt prompt, CancellationToken ct)
        {
            Card    = ApprovalCard.Build(prompt, RootDir);
            Details = prompt.Details;
            return Task.FromResult(ApprovalDecision.Deny);
        }

        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct) =>
            Task.FromResult(ApprovalDecision.Deny);
    }

    private string File(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static JsonElement Edits(params (string path, string oldC, string newC)[] edits) =>
        JsonSerializer.SerializeToElement(new
        {
            edits = edits.Select(e => new { path = e.path, old_content = e.oldC, new_content = e.newC }).ToArray(),
        });

    private async Task<ApprovalCardModel> ApplyEditsCard(string language, params (string path, string oldC, string newC)[] edits)
    {
        Strings.ApplyLanguage(language);
        var spy = new CardSpy(() => _dir);
        await new ApplyEditsTool(spy, new FileHistoryService(), () => _dir, smartFix: null)
            .ExecuteAsync(Edits(edits), CancellationToken.None);
        return spy.Card ?? throw new Xunit.Sdk.XunitException("no approval was asked");
    }

    [Fact]
    public async Task ATwoFileBatch_IsTitledWithTheCount_AndNamesBothFiles()
    {
        var a = File("alpha.txt", "one\n");
        var b = File("beta.txt", "two\n");

        var card = await ApplyEditsCard("en", (a, "one", "uno"), (b, "two", "dos"));
        Assert.Equal("Change 2 files", card.Title);
        Assert.Equal("alpha.txt, beta.txt", card.Subject);
        Assert.Equal("Apply edits to 2 files?", card.Preview[0].Text);

        var french = await ApplyEditsCard("fr", (a, "one", "uno"), (b, "two", "dos"));
        Assert.Equal("Modifier 2 fichiers", french.Title);
        Assert.Equal("Appliquer les modifications à 2 fichiers ?", french.Preview[0].Text);

        var russian = await ApplyEditsCard("ru", (a, "one", "uno"), (b, "two", "dos"));
        Assert.Equal("Изменить 2 файла", russian.Title);
    }

    [Fact]
    public async Task AOneFileBatch_StaysAChangeToAFile()
    {
        // Reference arm: one file is one file, named as before.
        var a = File("alpha.txt", "one\n");

        var card = await ApplyEditsCard("en", (a, "one", "uno"));

        Assert.Equal("Change a file", card.Title);
        Assert.Equal("alpha.txt", card.Subject);
        Assert.Equal("Apply edits to 1 file?", card.Preview[0].Text);
    }

    [Fact]
    public async Task ALongList_IsCutInTheHeader_AndCountedInTheTitle()
    {
        var edits = Enumerable.Range(0, 30)
            .Select(i => (File($"a-file-with-a-long-name-{i:D2}.txt", "x\n"), "x", "y")).ToArray();

        var card = await ApplyEditsCard("en", edits);

        Assert.Equal("Change 30 files", card.Title);
        Assert.EndsWith("…", card.Subject, StringComparison.Ordinal);
        Assert.Equal(ApprovalCard.SubjectChars + 1, card.Subject.Length);
    }

    [Fact]
    public async Task ARename_IsAskedInTheUsersLanguage_WithWhatTheScanMissed()
    {
        File("A.cs", "class A { void M() { OldName(); } }\n");
        File("B.cs", "class B { void N() { OldName(); } }\n");
        // Past the size cap: the scan does not open it, so the rename would leave its OldName behind.
        File("Big.cs", "class Big { void P() { OldName(); } }\n// " + new string('x', 210_000) + "\n");

        Strings.ApplyLanguage("fr");
        var spy = new CardSpy(() => _dir);
        await new RenameSymbolTool(spy, new FileHistoryService(), () => _dir)
            .ExecuteAsync(JsonDocument.Parse("""{"old_name":"OldName","new_name":"NewName","dry_run":false}""").RootElement,
                          CancellationToken.None);

        Assert.StartsWith("Renommer `OldName` → `NewName` dans 2 fichiers (2 occurrences)", spy.Details, StringComparison.Ordinal);
        Assert.Contains("⚠ Analyse partielle : seuls 2 fichiers source sur 3", spy.Details, StringComparison.Ordinal);
        Assert.Contains("Renommage textuel", spy.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARenameOverEveryFile_SaysNothingAboutTheScan()
    {
        // Reference arm: a scan that read every file adds no warning to the question.
        File("A.cs", "class A { void M() { OldName(); } }\n");

        Strings.ApplyLanguage("en");
        var spy = new CardSpy(() => _dir);
        await new RenameSymbolTool(spy, new FileHistoryService(), () => _dir)
            .ExecuteAsync(JsonDocument.Parse("""{"old_name":"OldName","new_name":"NewName","dry_run":false}""").RootElement,
                          CancellationToken.None);

        Assert.StartsWith("Rename `OldName` → `NewName` in 1 file (1 occurrence)", spy.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("Partial analysis", spy.Details, StringComparison.Ordinal);
    }
}
