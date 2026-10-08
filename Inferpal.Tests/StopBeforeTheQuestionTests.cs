using System.IO;
using Inferpal.Host;
using Inferpal.Services.Prompting;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A turn stopped while its context is built (workspace block, auto-context) never put its question in the history:
/// the host says so, the workspace block is still owed to the next question, and Regenerate takes nothing back.
/// </summary>
/// <remarks>
/// Two defects of one moment, in both editors. The "workspace block sent" flag was set when the block was BUILT: stopped
/// during the auto-context build that follows, the question — block included — was dropped, and no later question of the
/// session carried the block. And the question stayed on screen as an ordinary one, so Regenerate took back "the last
/// question of the history" — the one BEFORE, whose answer the screen still showed.
/// </remarks>
public partial class HostServerTests
{
    [Fact]
    public async Task ChatSend_StoppedDuringTheContextBuild_KeepsNoQuestion_AndTheNextOneCarriesTheWorkspaceBlock()
    {
        TestRagStore.Redirect();
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"host-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "App.sln"), "");   // the workspace block reads a solution
        await File.WriteAllTextAsync(Path.Combine(root, "Pricing.cs"), string.Join('\n',
            "public class Pricing", "{",
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
            var s = h.Server.CurrentSession!;
            s.ToolsEnabled = false;
            var before = s.History.Count;

            // The auto-context's query embedding is held until the turn is stopped.
            var held = new TaskCompletionSource();
            h.Fake.OnEmbeddingAsync = async (_, ct) =>
            {
                held.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            };
            using var stop = new CancellationTokenSource();
            var turn = h.Server.ChatSendAsync(new ChatSendParams("what does the pricing class compute?"), stop.Token);
            await held.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));   // witness: stopped DURING the build
            stop.Cancel();
            var stopped = await turn.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.True(stopped.Cancelled);
            Assert.False(stopped.QuestionKept);
            Assert.Equal(before, s.History.Count);
            Assert.False(s.WorkspaceContextSent);

            h.Fake.OnEmbeddingAsync = null;
            h.Fake.OnChat = (onToken, _) =>
            {
                onToken?.Invoke("an answer");
                return Task.FromResult(new Inferpal.Models.ChatTurnResult("an answer", null, 3, 5));
            };
            var next = await h.Server.ChatSendAsync(new ChatSendParams("and what does it return?"), CancellationToken.None)
                                     .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.True(next.QuestionKept);
            Assert.True(WorkspaceContext.IsIn(s.History), "the workspace block was never sent: the stopped question took it.");
        }
        finally
        {
            h.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ── The adapters: Regenerate takes back only a question that reached the history ──────────────────────────

    [Fact]
    public void VsCode_MarksAQuestionTheHostDidNotKeep_AsANotice()
    {
        var src    = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
                         File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", "chatViewProvider.ts")));
        var start  = src.IndexOf("} else if (result.cancelled) {", StringComparison.Ordinal);
        Assert.True(start >= 0, "the stopped-turn branch was not found: this test would have measured nothing.");
        var branch = src[start..src.IndexOf("t('Cancelled.')", start, StringComparison.Ordinal)];

        Assert.Contains("result.questionKept === false", branch, StringComparison.Ordinal);
        Assert.Contains("asked.notice = true", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudio_RollsBackOnlyAQuestionThatEnteredTheHistory_AndOwesTheBlockUntilThen()
    {
        static string Method(string file, string name)
        {
            var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow", file);
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
            return root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == name).ToString();
        }

        var regen    = Method("InferpalToolWindowData.ToolInvocation.cs", "RegenerateAsync");
        var guard    = regen.IndexOf("ReferenceEquals(_questionInHistory, lastUserItem)", StringComparison.Ordinal);
        var rollback = regen.IndexOf("_history.RemoveRange(", StringComparison.Ordinal);
        Assert.True(rollback > 0, "the rollback was not found: this test would have measured nothing.");
        Assert.True(guard > 0 && guard < rollback, "Regenerate rolls the history back without checking the question is in it.");

        var send  = Method("InferpalToolWindowData.ChatTurn.cs", "SendCoreAsync");
        var added = send.IndexOf("_history.Add(new ChatMessageDto(\"user\", historyText))", StringComparison.Ordinal);
        var flag  = send.IndexOf("_workspaceContextInjected = true", StringComparison.Ordinal);
        Assert.True(added > 0, "the question's entry into the history was not found.");
        Assert.True(flag > added, "the workspace block is marked sent before the question carrying it is in the history.");
        Assert.Contains("_questionInHistory = _lastSent?.Question", send[added..], StringComparison.Ordinal);
    }
}
