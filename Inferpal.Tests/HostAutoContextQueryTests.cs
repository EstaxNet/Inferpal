using System.IO;
using Inferpal.Host;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The per-turn auto-context searched VS Code's EXPANDED prompt — the question plus the body of every attached file
/// (up to 40,000 characters each) — where Visual Studio searches the question the user typed. Retrieval was steered by
/// the attachment instead of the question, the shadow pre-computed on the typed text never matched, and an embedding
/// model with a small window was sent the whole of it.
/// </summary>
public partial class HostServerTests
{
    private const string AttachedBody = "ATTACHED-BODY-MARKER";

    private async Task<FakeInferenceProvider> AskWithAnAttachmentAsync(object send)
    {
        TestRagStore.Redirect();
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"host-query-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Fine.cs"), string.Join('\n',
            "public class Fine", "{",
            "    public int One()   => 1;", "    public int Two()   => 2;", "    public int Three() => 3;",
            "    public int Four()  => 4;", "    public int Five()  => 5;", "    public int Six()   => 6;", "}"));
        var h = CreateHarness(cfg =>
        {
            cfg.RagEnabled = true;
            cfg.RagEmbeddingModel = "embed-model";
            cfg.RagAutoContextEnabled = true;
        });
        try
        {
            h.Fake.OnEmbedding = _ => [0.1f, 0.2f, 0.3f];
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            for (var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30); DateTime.UtcNow < deadline; await Task.Delay(50))
            {
                var status = await h.Client.InvokeAsync<IndexStatusResult>("index/status");
                if (status.ChunkCount > 0 && !status.IsIndexing) break;
            }
            await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", send)
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            return h.Fake;
        }
        finally
        {
            h.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string Expanded(string question) =>
        question + "\n\n## Attached file: big.txt\n```\n" + string.Concat(Enumerable.Repeat(AttachedBody + " ", 400)) + "\n```";

    [Fact]
    public async Task TheAutoContext_SearchesTheQuestion_NotTheAttachedFiles()
    {
        const string question = "which method of Fine returns three";
        var fake = await AskWithAnAttachmentAsync(new { prompt = Expanded(question), query = question, agentMode = false });

        var queries = fake.EmbeddingRequests.Select(r => r.Text).Where(t => !t.Contains("public class Fine")).ToList();
        Assert.Contains(queries, t => t.Contains(question, StringComparison.Ordinal));   // WITNESS: a search ran
        Assert.DoesNotContain(queries, t => t.Contains(AttachedBody, StringComparison.Ordinal));
    }

    [Fact]
    public void VsCode_SendsTheQuestion_BesideTheExpandedPrompt()
    {
        var turn = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("chatViewProvider.ts"), "private async chatTurn(");
        Assert.Contains("expandMentions(prompt)", turn, StringComparison.Ordinal);   // WITNESS: the prompt is expanded here
        Assert.Contains("query: query ?? prompt", turn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdapterThatSendsNoQuestion_StillGetsTheAutoContext()
    {
        // REFERENCE ARM: an older adapter sends the prompt alone — it is searched, as before.
        const string question = "which method of Fine returns three";
        var fake = await AskWithAnAttachmentAsync(new { prompt = question, agentMode = false });

        Assert.Contains(fake.EmbeddingRequests, r => r.Text.Contains(question, StringComparison.Ordinal));
    }
}
