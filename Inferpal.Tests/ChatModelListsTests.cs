using System.IO;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A model picked as the chat, agent or task model is asked questions: an embedding model answers none, and every
/// turn then ends on the server's refusal. Every list offered for one of those boxes is the chat half of a
/// <see cref="ModelsByKind"/>, the code-search box its embedding half — one split for Visual Studio, VS Code and the
/// settings window of an ACP client (by name, <see cref="ModelCatalog.ChatModelsOf"/>, when the server publishes
/// nothing; <c>PublishedModelCapabilitiesTests</c> when it does). The VS Code panel (which the
/// ACP window reuses) offered every listed model in every box, VS Code's chat picker and Visual Studio's header menu
/// too. The host side is measured in <c>HostServerTests.ModelsListByKind_*</c>; these rules read the editors' sources
/// (the extension has no TypeScript test runner).
/// </summary>
public class ChatModelListsTests
{
    [Fact]
    public void TheCatalog_SplitsAListing_IntoChatAndEmbeddingModels()
    {
        string[] listed = ["qwen3-coder:30b", "nomic-embed-text", "devstral-small-2", "bge-m3", "embeddinggemma"];
        Assert.Equal(["qwen3-coder:30b", "devstral-small-2"], ModelCatalog.ChatModelsOf(listed));
        Assert.Equal(["nomic-embed-text", "bge-m3", "embeddinggemma"], ModelCatalog.EmbeddingModelsOf(listed));

        // Reference arm: a server with chat models only keeps all of them, in its order.
        string[] chatOnly = ["gemma-3-12b", "devstral-small-2"];
        Assert.Equal(chatOnly, ModelCatalog.ChatModelsOf(chatOnly));
        Assert.Empty(ModelCatalog.EmbeddingModelsOf(chatOnly));
    }

    /// <summary>The schema says which box lists the embedding models: the code-search model, and no other.</summary>
    [Fact]
    public void OnlyTheCodeSearchModelBox_OffersTheEmbeddingModels()
    {
        var modelBoxes = SettingsSchema.AllFields.Where(f => f.Kind == SettingKind.Model).ToList();
        Assert.True(modelBoxes.Count >= 6, $"only {modelBoxes.Count} model boxes read: the schema is no longer measured");   // WITNESS
        Assert.Equal(["ragEmbeddingModel"], modelBoxes.Where(f => f.EmbeddingModels).Select(f => f.Key));
    }

    [Fact]
    public void TheVsCodeForm_OffersEachBoxItsOwnList()
    {
        var settings = WebviewRebuildTests.TsCode("webview/settings.ts");
        var popup = WebviewRebuildTests.Body(settings, "function renderModelPopup(");
        Assert.Contains("const listed = modelPopupTarget?.dataset.list === 'embedding' ? embeddingModels : models;", popup,
                        StringComparison.Ordinal);
        Assert.Contains("? ['', ...listed] : listed;", popup, StringComparison.Ordinal);
        Assert.Contains("if (field.embeddingModels) {\n          box.dataset.list = 'embedding';", settings.Replace("\r\n", "\n"),
                        StringComparison.Ordinal);

        // Both lists come from the host's split, on the first load and on every refresh.
        var bridge = WebviewRebuildTests.TsCode("settingsBridge.ts");
        Assert.Equal(2, CountOf(bridge, "host.modelsListByKind("));
        Assert.Contains("models: models.chat,", bridge, StringComparison.Ordinal);
        Assert.Contains("embeddingModels: models.embedding,", bridge, StringComparison.Ordinal);
        Assert.Contains("models: listed.chat, embeddingModels: listed.embedding", bridge, StringComparison.Ordinal);
    }

    /// <summary>No VS Code surface offers the raw listing: <c>models/list</c> is read by the client wrapper alone.</summary>
    [Fact]
    public void NoVsCodeSurface_OffersTheRawListing()
    {
        var root = Path.Combine(WebviewRebuildTests.RepoRoot(), "vscode", "src");
        var files = Directory.EnumerateFiles(root, "*.ts", SearchOption.AllDirectories).ToList();
        Assert.True(files.Count > 20, $"only {files.Count} TypeScript files read: the sources moved");   // WITNESS
        var offenders = files
            .Where(f => !f.EndsWith("hostClient.ts", StringComparison.Ordinal))
            .Where(f => WebviewRebuildTests.TsCode(Path.GetRelativePath(root, f)).Contains(".modelsList(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.Empty(offenders);

        // The chat picker takes the chat half, at each of its four reads.
        var chat = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        Assert.Equal(4, CountOf(chat, "(await host.modelsListByKind()).chat"));
        Assert.Equal(CountOf(chat, "modelsListByKind("), CountOf(chat, "(await host.modelsListByKind()).chat"));
    }

    /// <summary>Visual Studio's two lists — the settings boxes and the chat header's menu — go through the catalog.</summary>
    [Fact]
    public void TheVisualStudioLists_GoThroughTheCatalog()
    {
        var settings = ConventionCoverageTests.CodeOnly(Path.Combine(WebviewRebuildTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));
        Assert.Contains("var models = await client.ListModelsByKindAsync(ct, url);", settings, StringComparison.Ordinal);
        Assert.Contains("var chatModels = models.Chat;", settings, StringComparison.Ordinal);
        Assert.Contains("SelectionPreservingList.Sync(AvailableModels, chatModels, [current]);", settings, StringComparison.Ordinal);
        Assert.Contains("SelectionPreservingList.Sync(AvailableOptionalModels, chatModels,", settings, StringComparison.Ordinal);
        Assert.Contains("SelectionPreservingList.Sync(AvailableEmbeddingModels, models.Embedding,", settings, StringComparison.Ordinal);

        var chrome = ConventionCoverageTests.CodeOnly(Path.Combine(WebviewRebuildTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"));
        Assert.Contains("listed = await _client.ListModelsByKindAsync(ct);", chrome, StringComparison.Ordinal);
        Assert.Contains("foreach (var name in listed.Chat)", chrome, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
            n++;
        return n;
    }
}
