using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The semantic index is one more reader whose text the model quotes into an edit: <c>search_codebase</c> results and
/// the automatic "## Relevant code" block are code, copied back as an <c>old_content</c>. It read files as UTF-8 — a
/// Windows-1252 source came back with "�" where the edit tools now decode "é", so the quote answered "not found".
/// </summary>
public sealed class LegacyEncodingIndexTests : IDisposable
{
    private readonly string _root;
    private ProjectIndexService? _index;

    public LegacyEncodingIndexTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"legacy-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        // Windows-1252: the é of "crème" and "café" is one byte, 0xE9 / 0xE8, not valid UTF-8.
        File.WriteAllBytes(Path.Combine(_root, "Cafe.cs"), Encoding.Latin1.GetBytes(string.Join("\r\n",
            "public class Cafe",
            "{",
            "    // café crème : the widget of the morning",
            "    public int Widget()  => 1;",
            "    public int Two()     => 2;",
            "    public int Three()   => 3;",
            "    public int Four()    => 4;",
            "}") + "\r\n"));
    }

    public void Dispose()
    {
        _index?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task ASearchResult_ShowsTheAccent_SoItCanBeQuotedIntoAnEdit()
    {
        var config = new InferpalConfig { RagEnabled = true, RagTopK = 5 };
        var client = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };
        _index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _index.StartIndexing(_root);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);   // this test WAITS, it measures no overrun
        while ((_index.IsIndexing || _index.ChunkCount == 0) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(_index.ChunkCount > 0, $"nothing was indexed: {_index.Status}");                     // witness

        var report = await new SemanticSearchTool(_index, client, config)
            .ExecuteAsync(JsonDocument.Parse("""{"query":"Widget"}""").RootElement, CancellationToken.None);

        Assert.Contains("// caf", report);                                                           // witness
        Assert.DoesNotContain("�", report);
    }
}
