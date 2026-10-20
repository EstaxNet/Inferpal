using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Services.Mcp;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The card an MCP server's form is filled on, across the host's wire (<c>input/form</c>, <c>chat/inputReceipt</c>): what
/// VS Code's extension is sent and answers — the shapes <c>vscode/src/protocol.ts</c> mirrors.
/// </summary>
public class RpcInputFormTests
{
    private sealed class Adapter
    {
        private int _shown;
        public List<JsonElement> Cards { get; } = [];
        public List<(string Id, string Saved)> Receipts { get; } = [];
        public TaskCompletionSource ReceiptSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        [JsonRpcMethod("input/form")]
        public object InputForm(JsonElement card)
        {
            lock (Cards) Cards.Add(card.Clone());
            // First a value the schema refuses, then one it takes.
            var days = Interlocked.Increment(ref _shown) == 1 ? "45" : "12";
            return new { action = "accept", values = new Dictionary<string, object> { ["days"] = days, ["draft"] = true } };
        }

        [JsonRpcMethod("chat/inputReceipt")]
        public void Receipt(string id, string title, string detail, string body, string saved)
        {
            lock (Receipts) Receipts.Add((id, saved));
            ReceiptSeen.TrySetResult();
        }
    }

    private static (RpcApprovalService Service, Adapter Adapter, IDisposable Rpc) Pair(bool inputCards, bool turnRuns)
    {
        var (a, b) = FullDuplexStream.CreatePair();
        var host = HostRpc.Create(a, a);
        var adapter = new Adapter();
        var editor = HostRpc.Create(b, b, adapter);
        host.StartListening();
        editor.StartListening();
        var service = new RpcApprovalService(new InferpalConfig(), () => null, host, inputCards: inputCards) { TurnRuns = () => turnRuns };
        return (service, adapter, new Both(host, editor));
    }

    private sealed class Both(JsonRpc a, JsonRpc b) : IDisposable
    {
        public void Dispose()
        {
            try { a.Dispose(); } catch { }
            try { b.Dispose(); } catch { }
        }
    }

    private static McpInputForm Form()
    {
        using var doc = JsonDocument.Parse("""
            {"message":"How long?","requestedSchema":{"type":"object","properties":{
              "days":{"type":"integer","title":"Days","minimum":1,"maximum":30},"draft":{"type":"boolean"}},"required":["days"]}}
            """);
        return McpInputForm.Read("releases", "publish", doc.RootElement, out _)!;
    }

    [Fact]
    public async Task TheAdapterIsSentTheCard_ThenTheSameCardWithWhatToFix_AndTheLineItLeaves()
    {
        var (service, adapter, rpc) = Pair(inputCards: true, turnRuns: true);
        using (rpc)
        {
            Assert.True(service.CanAskForInput);
            var answer = await service.AskForInputAsync(Form(), CancellationToken.None);

            Assert.Equal(McpInputAction.Accept, answer.Action);
            Assert.Equal("""{"days":12,"draft":true}""", answer.Content!.ToJsonString());
            Assert.Equal(2, adapter.Cards.Count);
            var first = adapter.Cards[0];
            var again = adapter.Cards[1];
            // The names protocol.ts reads.
            foreach (var name in new[] { "id", "server", "title", "subtitle", "message", "fields", "errorSummary", "requiredNote", "reminder",
                                         "sendLabel", "declineLabel", "declineTooltip", "cancelLabel", "cancelTooltip" })
                Assert.True(first.TryGetProperty(name, out _), name);
            var field = first.GetProperty("fields")[0];
            foreach (var name in new[] { "key", "kind", "inputType", "label", "hint", "required", "warns", "buttons", "text", "flag", "options", "error" })
                Assert.True(field.TryGetProperty(name, out _), name);
            Assert.Equal(("number", "Days", ""), (field.GetProperty("kind").GetString(), field.GetProperty("label").GetString(), field.GetProperty("error").GetString()));
            Assert.Equal(first.GetProperty("id").GetString(), again.GetProperty("id").GetString());
            Assert.Equal("45", again.GetProperty("fields")[0].GetProperty("text").GetString());
            Assert.NotEqual("", again.GetProperty("fields")[0].GetProperty("error").GetString());
            Assert.NotEqual("", again.GetProperty("errorSummary").GetString());

            await adapter.ReceiptSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var (id, saved) = Assert.Single(adapter.Receipts);
            Assert.Equal(first.GetProperty("id").GetString(), id);
            Assert.Contains("Days: 12", saved);
        }
    }

    private sealed class RetiringAdapter
    {
        public TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Receipts { get; } = [];
        public TaskCompletionSource ReceiptSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // As VS Code's extension does: the cancellation retires the card, and the request is answered null.
        [JsonRpcMethod("input/form")]
        public async Task<object?> InputForm(JsonElement card, CancellationToken ct)
        {
            Shown.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            return null;
        }

        [JsonRpcMethod("chat/inputReceipt")]
        public void Receipt(string id, string title, string detail, string body, string saved)
        {
            lock (Receipts) Receipts.Add(title);
            ReceiptSeen.TrySetResult();
        }
    }

    [Fact]
    public async Task StoppedWhileTheCardWaits_ARetiredCardAnsweredNull_IsAStop_NeverACardThatCouldNotBeDrawn()
    {
        var (a, b) = FullDuplexStream.CreatePair();
        using var host = HostRpc.Create(a, a);
        var adapter = new RetiringAdapter();
        using var editor = HostRpc.Create(b, b, adapter);
        host.StartListening();
        editor.StartListening();
        var service = new RpcApprovalService(new InferpalConfig(), () => null, host, inputCards: true) { TurnRuns = () => true };

        using var stop = new CancellationTokenSource();
        var asked = service.AskForInputAsync(Form(), stop.Token);
        await adapter.Shown.Task.WaitAsync(TimeSpan.FromSeconds(30));
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked);
        await adapter.ReceiptSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Inferpal.Localization.Strings.InputReceiptStopped("releases"), Assert.Single(adapter.Receipts));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AServerMayAskOnlyAnAdapterThatDrawsTheCard_DuringATurn(bool inputCards, bool turnRuns)
    {
        // An Agent Client Protocol session initializes without the card; between turns no adapter draws one.
        var (service, _, rpc) = Pair(inputCards, turnRuns);
        using (rpc) Assert.False(service.CanAskForInput);
    }

    [Fact]
    public void VsCode_DrawsACardPostedAgainAfterARebuild_InTheNewView()
    {
        // A rebuild (hydrate) empties the view and posts the waiting cards again: replaced into the old, detached
        // element, the card never showed, and the host waited on it.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Inferpal.sln"))) root = root.Parent;
        var main = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(root!.FullName, "vscode", "src", "webview", "main.ts")));
        var render = main[main.IndexOf("function renderTranscript(", StringComparison.Ordinal)..];
        Assert.Contains("inputCards.clear();", render[..render.IndexOf("for (const item of transcript)", StringComparison.Ordinal)], StringComparison.Ordinal);
        var add = main[main.IndexOf("function addInputCard(", StringComparison.Ordinal)..];
        Assert.Contains("inputCards.get(card.id)?.isConnected", add[..add.IndexOf("const el =", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("if (card?.isConnected) {", main[main.IndexOf("function showInputReceipt(", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("decline", "Decline")]
    [InlineData("setAside", "SetAside")]
    [InlineData("cancel", "Cancel")]
    [InlineData("something new", "Cancel")]
    public void AnAnswerTheHostDoesNotKnow_IsACancel_NeverASend(string action, string expected) =>
        Assert.Equal(expected, new RpcApprovalService.InputFormAnswer(action, null).ToReply().Action.ToString());
}
