using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Inferpal.Services.Persistence;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Command logic that used to exist twice — once in the Visual Studio view-model, once in the
/// host — and now lives in a single Core handler. These tests pin the shared behaviour so the
/// two front-ends can only drift by changing this file.
/// </summary>
public class SharedCommandHandlerTests
{
    // ── /undo-run ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UndoRun_NoRunWithChanges_SaysSo()
    {
        var history = new FileHistoryService();

        var message = await UndoRunCommandHandler.HandleAsync(history, ["/undo-run"], root: null, CancellationToken.None);

        Assert.Equal(Strings.UndoRunNone, message);
    }

    [Fact]
    public async Task UndoRunList_EmptyHistory_SaysSo()
    {
        var history = new FileHistoryService();

        var message = await UndoRunCommandHandler.HandleAsync(history, ["/undo-run", "list"], null, CancellationToken.None);

        Assert.Equal(Strings.UndoRunNone, message);
    }

    [Fact]
    public async Task UndoRunList_ListsRunsThatTouchedFiles()
    {
        var history = new FileHistoryService();
        var dir     = Directory.CreateTempSubdirectory("inferpal-undo-");
        try
        {
            var file = Path.Combine(dir.FullName, "a.txt");
            await File.WriteAllTextAsync(file, "before");

            history.BeginRun();
            await history.SnapshotAsync(file, CancellationToken.None);   // records the pre-edit content
            await File.WriteAllTextAsync(file, "after");

            var message = await UndoRunCommandHandler.HandleAsync(
                history, ["/undo-run", "list"], dir.FullName, CancellationToken.None);

            Assert.Contains(Strings.UndoRunListHeader(1), message);
            Assert.Contains("file(s)", message);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task UndoRun_RestoresTheFileAndReportsItRelativeToTheRoot()
    {
        var history = new FileHistoryService();
        var dir     = Directory.CreateTempSubdirectory("inferpal-undo-");
        try
        {
            var file = Path.Combine(dir.FullName, "src", "a.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "before");

            history.BeginRun();
            await history.SnapshotAsync(file, CancellationToken.None);
            await File.WriteAllTextAsync(file, "after");

            var message = await UndoRunCommandHandler.HandleAsync(
                history, ["/undo-run"], dir.FullName, CancellationToken.None);

            Assert.Equal("before", await File.ReadAllTextAsync(file));
            Assert.Contains(Path.Combine("src", "a.txt"), message);      // relativised, not absolute
            Assert.DoesNotContain(dir.FullName, message);
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── /history ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task History_SearchWithNoHit_ReportsTheTerm()
    {
        // The store is the developer's real session folder; a random term is the one query
        // whose answer never depends on what happens to be saved there.
        var term = "zz-" + Guid.NewGuid().ToString("N");

        var message = await HistoryCommandHandler.HandleAsync(
            new ConversationStore(), ["/history", term], DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(Strings.HistoryNoResults(term), message);
    }

    [Fact]
    public async Task History_MultiWordSearch_KeepsTheWholeTerm()
    {
        var a = "zz-" + Guid.NewGuid().ToString("N");
        var b = "yy-" + Guid.NewGuid().ToString("N");

        var message = await HistoryCommandHandler.HandleAsync(
            new ConversationStore(), ["/history", a, b], DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(Strings.HistoryNoResults($"{a} {b}"), message);
    }

    // ── /index ─────────────────────────────────────────────────────────────────

    private static InferpalConfig RagConfig(bool enabled) => new() { RagEnabled = enabled, RagTopK = 7 };

    [Fact]
    public void Index_RagDisabled_ExplainsHowToEnableIt()
    {
        var message = IndexCommandHandler.Handle(Index(), RagConfig(enabled: false), ["/index"], root: @"C:\proj");

        Assert.Contains("**disabled**", message);
        Assert.Contains("ragEnabled", message);
    }

    [Fact]
    public void Index_NotStarted_PointsAtRebuild()
    {
        var message = IndexCommandHandler.Handle(Index(), RagConfig(enabled: true), ["/index"], @"C:\proj");

        Assert.Contains("/index rebuild", message);
    }

    [Fact]
    public void IndexRebuild_WithoutARoot_RefusesInsteadOfIndexingNothing()
    {
        var message = IndexCommandHandler.Handle(Index(), RagConfig(true), ["/index", "rebuild"], root: "");

        Assert.StartsWith("⚠", message);
    }

    private static ProjectIndexService Index() =>
        new(new FakeInferenceProvider(), RagConfig(enabled: true), new Inferpal.Services.Lsp.LspSemanticProvider());

    // ── /docs ──────────────────────────────────────────────────────────────────

    private static DocsIndexService Docs(InferpalConfig config) => new(new FakeInferenceProvider(), config);

    [Fact]
    public async Task Docs_AddWithoutAUrl_ShowsTheUsage()
    {
        var config = new InferpalConfig { DocSitesJson = "" };

        var message = await DocsCommandHandler.HandleAsync(
            config, Docs(config), ["/docs", "add"], new Progress<string>(), CancellationToken.None);

        Assert.Equal(Strings.DocsUsage, message);
    }

    [Fact]
    public async Task Docs_AddWithANonHttpUrl_ShowsTheUsage()
    {
        var config = new InferpalConfig { DocSitesJson = "" };

        var message = await DocsCommandHandler.HandleAsync(
            config, Docs(config), ["/docs", "add", "file:///etc/passwd"], new Progress<string>(), CancellationToken.None);

        Assert.Equal(Strings.DocsUsage, message);
    }

    [Fact]
    public async Task Docs_ListWithNoSource_SaysSo()
    {
        var config = new InferpalConfig { DocSitesJson = "" };

        var message = await DocsCommandHandler.HandleAsync(
            config, Docs(config), ["/docs"], new Progress<string>(), CancellationToken.None);

        Assert.Equal(Strings.DocsNoSites, message);
    }

    [Fact]
    public async Task Docs_RemoveUnknownId_SaysThereIsNothingToRemove()
    {
        var config = new InferpalConfig { DocSitesJson = "" };

        var message = await DocsCommandHandler.HandleAsync(
            config, Docs(config), ["/docs", "remove", "nope"], new Progress<string>(), CancellationToken.None);

        Assert.Equal(Strings.DocsNoSites, message);
    }

    // ── /template ──────────────────────────────────────────────────────────────

    [Fact]
    public void Template_NoArgument_ListsThePresets()
    {
        var result = TemplateCommandHandler.Handle(["/template"]);

        Assert.Null(result.Apply);
        Assert.Contains("code-review", result.Message);
    }

    [Fact]
    public void Template_KnownId_ReturnsThePresetToApply()
    {
        var result = TemplateCommandHandler.Handle(["/template", "BUG-HUNT"]);   // case-insensitive

        Assert.Null(result.Message);
        Assert.Equal("bug-hunt", result.Apply!.Id);
        Assert.NotEmpty(result.Apply.SystemSuffix);
    }

    [Fact]
    public void Template_UnknownId_ExplainsInsteadOfApplying()
    {
        var result = TemplateCommandHandler.Handle(["/template", "nope"]);

        Assert.Null(result.Apply);
        Assert.Contains("nope", result.Message);
    }

    // ── /context & /memory ─────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectFile_NoRoot_ReportsTheMissingSolution()
    {
        var message = await ProjectFileCommandHandler.HandleAsync(
            null, "context.md", p => $"missing {p}", (p, n, t) => t, CancellationToken.None);

        Assert.Equal(Strings.SlashContextNoSln, message);
    }

    [Fact]
    public async Task ProjectFile_MissingFile_UsesTheNotFoundBuilder()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-ctx-");
        try
        {
            var message = await ProjectFileCommandHandler.HandleAsync(
                dir.FullName, "context.md", p => $"missing:{p}", (p, n, t) => t, CancellationToken.None);

            Assert.StartsWith("missing:", message);
            Assert.Contains(".inferpal", message);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProjectFile_LongFile_IsPreviewedAndCapped()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-ctx-");
        try
        {
            var path = Path.Combine(dir.FullName, ".inferpal", "memory.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, new string('x', 1000));

            var message = await ProjectFileCommandHandler.HandleAsync(
                dir.FullName, "memory.md", p => "missing", (p, n, preview) => $"{n}|{preview}", CancellationToken.None);

            Assert.StartsWith("1000|", message);
            Assert.EndsWith("…", message);
            Assert.Equal("1000|".Length + 400 + 1, message.Length);   // preview capped, then the ellipsis
        }
        finally { dir.Delete(recursive: true); }
    }
}
