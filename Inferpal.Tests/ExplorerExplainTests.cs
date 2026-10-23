using System.IO;
using Inferpal.Localization;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The Explorer's "Explain": a file as the editor has it, or a folder — a project being its folder — as <c>@folder</c>
/// attaches it; the refusals those mentions give; sized for the answering model's window.
/// </summary>
public sealed class ExplorerExplainTests : IDisposable
{
    private const int Budget = 60_000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"explain-{Guid.NewGuid():N}");

    public ExplorerExplainTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Billing"));
        File.WriteAllText(Path.Combine(_root, "Billing", "Invoice.cs"), "class Invoice { decimal Total() => 42m; }\n");
        File.WriteAllText(Path.Combine(_root, "Billing", "Billing.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task AFile_IsExplainedFromItsText()
    {
        var file = Path.Combine(_root, "Billing", "Invoice.cs");

        var ask = await ExplorerExplain.ForAsync(file, _root, overlay: null, Budget, CancellationToken.None);

        Assert.Null(ask.Refusal);
        Assert.Equal((Strings.ExplainFileQuestion("Invoice.cs"), "Invoice.cs"), (ask.Question, ask.Chip));
        Assert.Contains("decimal Total() => 42m;", ask.Content);
    }

    [Fact]
    public async Task AFolder_AndAProjectThatIsItsFolder_AreExplainedFromWhatAtFolderAttaches()
    {
        var folder  = await ExplorerExplain.ForAsync(Path.Combine(_root, "Billing"), _root, overlay: null, Budget, CancellationToken.None);
        // Solution Explorer's project node: Visual Studio's adapter makes it its folder.
        var project = await ExplorerExplain.ForAsync(ExplorerExplain.ItemOf(Path.Combine(_root, "Billing", "Billing.csproj")), _root,
                                                     overlay: null, Budget, CancellationToken.None);

        Assert.Equal(Strings.ExplainFolderQuestion("Billing"), folder.Question);
        Assert.Contains("Invoice.cs", folder.Content);
        Assert.Contains("decimal Total() => 42m;", folder.Content);
        Assert.Equal((folder.Question, folder.Chip, folder.Content), (project.Question, project.Chip, project.Content));
    }

    [Fact]
    public async Task AFileTheIgnoreFilesExclude_IsRefused_LikeAtFile()
    {
        File.WriteAllText(Path.Combine(_root, ".aiignore"), "Billing/Invoice.cs\n");
        ContentExclusions.Declare(_root, () => ContentExclusions.DefaultFiles);   // this test's own root
        var file = Path.Combine(_root, "Billing", "Invoice.cs");

        var ask = await ExplorerExplain.ForAsync(file, _root, overlay: null, Budget, CancellationToken.None);

        Assert.NotNull(ask.Refusal);
        Assert.Equal(MentionController.FileRefusal(file, _root), ask.Refusal);
        Assert.Empty(ask.Content);
    }

    [Fact]
    public async Task TheAttachment_FitsTheAnsweringModelsWindow_AndItsChipSaysWhenItIsCut()
    {
        var file = Path.Combine(_root, "Billing", "Long.cs");
        File.WriteAllText(file, string.Join("\n", Enumerable.Range(1, 400).Select(i => $"// line {i} {new string('x', 40)}")));

        var ask = await ExplorerExplain.ForAsync(file, _root, overlay: null, budgetChars: 800, CancellationToken.None);

        Assert.InRange(ask.Content.Length, 1, 1_000);
        Assert.NotEqual("Long.cs", ask.Chip);
        Assert.StartsWith("Long.cs", ask.Chip);
    }

    [Fact]
    public async Task ABinaryFile_AndAFileGoneFromDisk_AreRefused_WithTheirCause()
    {
        var icon = Path.Combine(_root, "Billing", "app.ico");
        File.WriteAllBytes(icon, [0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x10, 0x10, 0x00, 0x00]);
        var gone = Path.Combine(_root, "Billing", "Removed.cs");   // a project item missing on disk

        var binary  = await ExplorerExplain.ForAsync(icon, _root, overlay: null, Budget, CancellationToken.None);
        var missing = await ExplorerExplain.ForAsync(gone, _root, overlay: null, Budget, CancellationToken.None);

        Assert.Equal(Strings.AttachBinaryFile("app.ico"), binary.Refusal);
        Assert.NotNull(missing.Refusal);
        Assert.Empty(missing.Content);
    }

    [Fact]
    public async Task AFolderPastTheBudget_KeepsTheNotesThatSayWhatIsMissing()
    {
        for (var i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(_root, "Billing", $"Part{i}.cs"), string.Join("\n", Enumerable.Repeat($"// part {i}", 200)));

        var ask = await ExplorerExplain.ForAsync(Path.Combine(_root, "Billing"), _root, overlay: null, budgetChars: 3_000,
                                                 CancellationToken.None);

        Assert.Contains("(stopped at the 3,000-character budget", ask.Content.Replace(' ', ',').Replace(' ', ','));
    }

    [Fact]
    public void AProjectOrASolution_StandsForItsFolder_AFileForItself()
    {
        Assert.Equal(Path.Combine(_root, "Billing"), ExplorerExplain.ItemOf(Path.Combine(_root, "Billing") + Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(_root, "Billing"), ExplorerExplain.ItemOf(Path.Combine(_root, "Billing", "Billing.csproj")));
        Assert.Equal(_root, ExplorerExplain.ItemOf(Path.Combine(_root, "App.sln")));
        Assert.Equal(Path.Combine(_root, "Billing", "Invoice.cs"), ExplorerExplain.ItemOf(Path.Combine(_root, "Billing", "Invoice.cs")));
    }
}
