using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Host.SettingsWindow;
using Inferpal.Localization;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The settings window's host SHOWS the code index; it never builds one. Its pass walked and embedded the folder the
/// window was opened in — the whole home folder for <c>Inferpal.Host --settings</c> started from there —, and a window
/// opened by <c>/settings</c> ran a second indexer, in the thread's process, on the thread's database. Every setting
/// saved in the window started one too (the root was pinned but never indexed). It shows the index saved for the folder.
/// </summary>
public sealed class SettingsWindowIndexTests : IDisposable
{
    private readonly string _root  = Directory.CreateTempSubdirectory("inferpal-window-index").FullName;
    private readonly string _empty = Directory.CreateTempSubdirectory("inferpal-window-noindex").FullName;
    private readonly string _web   = Directory.CreateTempSubdirectory("inferpal-window-web").FullName;
    private readonly HttpClient _http = new();

    public SettingsWindowIndexTests()
    {
        TestRagStore.Redirect();
        File.WriteAllText(Path.Combine(_web, "settings.css"), "/* form */");
        File.WriteAllText(Path.Combine(_web, "settings-standalone.css"), "/* theme */");
        File.WriteAllText(Path.Combine(_web, "settings-standalone.js"), "/* bundle */");
        foreach (var dir in new[] { _root, _empty })
        {
            File.WriteAllText(Path.Combine(dir, "A.cs"), "namespace Demo;\n\npublic class A\n{\n    public int One() => 1;\n}\n");
            File.WriteAllText(Path.Combine(dir, "B.cs"), "namespace Demo;\n\npublic class B\n{\n    public int Two() => 2;\n}\n");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (var dir in new[] { _root, _empty, _web })
            try { Directory.Delete(dir, recursive: true); } catch { /* cleanup */ }
    }

    private static HostServer Host() =>
        new(_ => new FakeInferenceProvider { ModelNames = ["test-model"] },
            () => new InferpalConfig { RagEnabled = true, McpEnabled = false, DefaultModel = "test-model" });

    private async Task<JsonElement> CallAsync(SettingsWindowServer server, string method, object? args = null)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { method, @params = args }), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(server.Url + "rpc", content);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        return body.GetProperty("result");
    }

    /// <summary>The index card once its state is <paramref name="state"/>; fails at the deadline (a witness that gives up
    /// in silence sees nothing).</summary>
    private async Task<JsonElement> CardWhenAsync(SettingsWindowServer server, string state)
    {
        JsonElement card = default;
        for (var i = 0; i < 300; i++)
        {
            card = await CallAsync(server, "settings/indexCard");
            if (card.GetProperty("state").GetString() == state) return card;
            await Task.Delay(100);
        }
        Assert.Fail($"the index card never read '{state}': last {card}");
        return card;
    }

    [Fact]
    public async Task TheWindowShowsTheSavedIndex_AndNeverIndexesTheFolder()
    {
        // The index a chat saved for the folder: one of its two files.
        await new RagDatabase(_root).SaveAsync(
        [
            new RagChunk { FilePath = Path.Combine(_root, "A.cs"), RelPath = "A.cs", StartLine = 1, EndLine = 6,
                           Content = "public class A", ContentHash = "a" },
        ], CancellationToken.None);

        await using var server = await SettingsWindowServer.StartAsync(_root, _web, Host, CancellationToken.None);
        var index = server.Host.CurrentSession!.Index;
        Assert.Equal(string.Empty, index.IndexedRoot);   // no pass started (StartIndexing sets it at once)

        var card = await CardWhenAsync(server, "saved");
        Assert.Equal(Strings.IndexCardSaved, card.GetProperty("title").GetString());
        var lead = Strings.IndexCardSavedDetail(1, "\u0001").Split('\u0001')[0];
        Assert.StartsWith(lead, card.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // A setting saved in the window starts no pass either.
        var json = (await CallAsync(server, "config/get")).GetString()!;
        await CallAsync(server, "config/update", new { json, @base = json });
        Assert.Equal(string.Empty, index.IndexedRoot);
        Assert.Equal("saved", (await CallAsync(server, "settings/indexCard")).GetProperty("state").GetString());

        // Reference arm: the page's Rebuild indexes, on request — both files.
        await CallAsync(server, "settings/indexRebuild");
        Assert.Equal(_root, index.IndexedRoot);
        await CardWhenAsync(server, "ready");
    }

    [Fact]
    public async Task AFolderWithoutAnIndex_GetsNone_NotEvenAnEmptyDatabase()
    {
        await using var server = await SettingsWindowServer.StartAsync(_empty, _web, Host, CancellationToken.None);
        Assert.Equal(string.Empty, server.Host.CurrentSession!.Index.IndexedRoot);
        Assert.Equal("notBuilt", (await CallAsync(server, "settings/indexCard")).GetProperty("state").GetString());
        Assert.False(File.Exists(RagDatabase.PathFor(_empty)), "the window wrote an index database for a folder it only shows");
    }
}
