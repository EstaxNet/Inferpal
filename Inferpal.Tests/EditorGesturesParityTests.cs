using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The editor's gestures, the same in VS Code as in Visual Studio: Edit with AI sends one prompt from the Core whichever
/// editor asks; Explain and Add Tests are on the editor's menu; /test takes the selection and its tests are written by an
/// edit the editor can undo; the project map has its key.
/// </summary>
public class EditorGesturesParityTests
{
    // ── The block an edit applies to ──────────────────────────────────────────

    [Fact]
    public void ASelection_TakesTheIndentationOfItsFirstLine_ACaretTakesItsLine()
    {
        const string text = "class A\n{\n    int x = 1;\r\n    int y = 2;\n\n}\n";
        var first = text.IndexOf("int x", StringComparison.Ordinal);
        var end   = text.IndexOf("int y", StringComparison.Ordinal) + "int y = 2;".Length;

        Assert.Equal((first - 4, end), InlineEditPrompt.Target(text, first, end));        // the indentation goes with it
        var caret = text.IndexOf("x = 1", StringComparison.Ordinal);
        Assert.Equal("    int x = 1;", text[InlineEditPrompt.Target(text, caret, caret)!.Value.Start..InlineEditPrompt.Target(text, caret, caret)!.Value.End]);
        var blank = text.IndexOf("\n\n", StringComparison.Ordinal) + 1;
        Assert.Null(InlineEditPrompt.Target(text, blank, blank));                          // nothing to edit
    }

    // ── One prompt, both editors ──────────────────────────────────────────────

    [Fact]
    public void VisualStudiosEditWithAi_SendsTheCoresPrompt()
    {
        var command = ConventionCoverageTests.CodeOnly(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(),
                                                                     "Inferpal", "Commands", "InlineEditSelectionCommand.cs"));
        Assert.Contains("Services.CodeActions.InlineEditPrompt.System", command, StringComparison.Ordinal);
        Assert.Contains("Services.CodeActions.InlineEditPrompt.UserMessage(originalCode, instruction)", command, StringComparison.Ordinal);
        Assert.Contains("Services.CodeActions.InlineEditPrompt.Target(", command, StringComparison.Ordinal);
        Assert.DoesNotContain("You are an expert code editor", command, StringComparison.Ordinal);   // no second copy
    }

    // ── VS Code: the commands, the menu, the keys ─────────────────────────────

    [Fact]
    public void VsCode_OffersTheEditorsGestures_OnItsMenuAndKeys()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "vscode", "package.json")));
        var contributes = manifest.RootElement.GetProperty("contributes");
        var commands = contributes.GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("command").GetString()).ToList();
        var menu = contributes.GetProperty("menus").GetProperty("editor/context").EnumerateArray()
            .Select(c => c.GetProperty("command").GetString()).ToList();
        var keys = contributes.GetProperty("keybindings").EnumerateArray()
            .ToDictionary(k => k.GetProperty("command").GetString()!, k => k.GetProperty("key").GetString());

        foreach (var command in new[] { "inferpal.editSelection", "inferpal.explainSelection", "inferpal.testSelection", "inferpal.map" })
            Assert.Contains(command, commands);
        foreach (var command in new[] { "inferpal.editSelection", "inferpal.explainSelection", "inferpal.testSelection" })
            Assert.Contains(command, menu);
        // No key of its own in VS Code: Ctrl/Cmd+Shift+I is already the developer tools' and another assistant's chat
        // there — Edit with AI is in the editor's menu and the palette, and anyone binds the key they want.
        Assert.False(keys.ContainsKey("inferpal.editSelection"), "Edit with AI takes a key VS Code's users already have");
        Assert.Equal("alt+m", keys["inferpal.map"]);

        var extension = WebviewRebuildTests.TsCode("extension.ts");
        Assert.Contains("registerCommand('inferpal.explainSelection', () => chatView.runSlashCommand('/explain'))", extension, StringComparison.Ordinal);
        Assert.Contains("registerCommand('inferpal.testSelection', () => chatView.runSlashCommand('/test'))", extension, StringComparison.Ordinal);
        Assert.Contains("registerCommand('inferpal.map', () => chatView.runSlashCommand('/map'))", extension, StringComparison.Ordinal);
        Assert.Contains("registerCommand('inferpal.editSelection', () => chatView.editWithAi())", extension, StringComparison.Ordinal);
    }

    /// <summary>⚠ vscode-jsonrpc drops a trailing argument only when it IS a token: an <c>undefined</c> one goes on the
    /// wire as a second parameter, which the host's single-object methods refuse — every <c>/fix</c> sent without a token
    /// failed so. A request whose token is optional goes through <c>withToken</c>.</summary>
    [Fact]
    public void TheHostClient_NeverHandsAnOptionalTokenToSendRequest()
    {
        var client = WebviewRebuildTests.TsCode("hostClient.ts");
        Assert.Contains("...HostClient.withToken(params, token)", client, StringComparison.Ordinal);   // WITNESS
        // A token handed straight to sendRequest, after the method and its parameters.
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"sendRequest(<[^>]*>)?\(\s*'[^']*',\s*[\w.]+,\s*token\s*\)"), client);
    }

    [Fact]
    public void VsCodesTest_SendsTheSelection_AndWritesByAnEditItCanUndo()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        Assert.Contains("await this.runTestGeneration(host, ChatViewProvider.requestOf(prompt));", provider, StringComparison.Ordinal);
        var run = WebviewRebuildTests.Body(provider, "private async runTestGeneration(");
        Assert.Contains("const selected = editor.selection.isEmpty ? '' : document.getText(editor.selection);", run, StringComparison.Ordinal);
        Assert.Contains("const code = selected.trim() ? selected : document.getText();", run, StringComparison.Ordinal);
        Assert.Contains("vscode.workspace.applyEdit(edit)", run, StringComparison.Ordinal);
        Assert.DoesNotContain("writeFile", run, StringComparison.Ordinal);
        // A test file edited while the model works is not replaced under the edit.
        // A file open at the start: its version; one closed then, opened and typed in meanwhile: its unsaved edits.
        Assert.Contains("if (seen !== undefined ? seen !== tests.version : tests.isDirty || (result.extends != null && tests.getText() !== result.extends))",
                        run, StringComparison.Ordinal);
    }
}

public partial class HostServerTests
{
    /// <summary>Edit with AI from VS Code: the Core's prompt, the inline-edit model, the caret's line when nothing is
    /// selected — and a blank line refused with its reason, no model asked.</summary>
    [Fact]
    public async Task CodeActionRun_Edit_SendsTheCoresPrompt_ForTheCaretsLine()
    {
        using var h = CreateHarness(cfg => cfg.InlineEditModel = "editor-model");
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        List<ChatMessageDto>? sent = null;
        string? model = null;
        h.Fake.OnChatRequest = (m, messages, _, _) =>
        {
            (model, sent) = (m, messages);
            return Task.FromResult(new ChatTurnResult("    long total = 0;", null, 0, 0));
        };
        const string text = "void F()\n{\n    int total = 0;\n\n}\n";
        var caret = text.IndexOf("total", StringComparison.Ordinal);

        var edited = await h.Client.InvokeWithParameterObjectAsync<Host.CodeActionResultDto>(
                "codeAction/run", new { kind = "edit", text, selStart = caret, selEnd = caret, request = "use a long" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal("edited", edited.Outcome);
        Assert.Equal("editor-model", model);
        Assert.Equal(InlineEditPrompt.System, sent!.Single(m => m.Role == "system").Content);
        // Visual Studio's order, word for word: the code, then the instruction.
        Assert.Equal("Code to edit:\n    int total = 0;\n\nInstruction: use a long", sent!.Single(m => m.Role == "user").Content);

        sent = null;
        var blank = text.IndexOf("\n\n", StringComparison.Ordinal) + 1;
        var refused = await h.Client.InvokeWithParameterObjectAsync<Host.CodeActionResultDto>(
                "codeAction/run", new { kind = "edit", text, selStart = blank, selEnd = blank, request = "use a long" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Equal(("refused", Strings.InlineEditNothingToEdit), (refused.Outcome, refused.FailureDetail));
        Assert.Null(sent);
    }

    /// <summary>Edit with AI runs outside the chat: a question typed while it runs is told it is Edit with AI that holds
    /// the slot — "a turn is running" would point at a turn the chat does not show.</summary>
    [Fact]
    public async Task AQuestionDuringEditWithAi_IsToldWhatHoldsTheSlot()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.Fake.OnChatRequest = async (_, _, _, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new ChatTurnResult("int y = 2;", null, 0, 0);
        };
        var edit = h.Client.InvokeWithParameterObjectAsync<Host.CodeActionResultDto>(
            "codeAction/run", new { kind = "edit", text = "int x = 1;", selStart = 0, selEnd = 0, request = "rename x" });
        await started.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var refused = await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() =>
            h.Client.InvokeWithParameterObjectAsync<Host.ChatSendResult>("chat/send", new { prompt = "hello", agentMode = false })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs)));
        release.SetResult();
        await edit.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(Strings.HostEditBusy, refused.Message);
    }

    /// <summary>/test from VS Code: the code the editor sends (its selection) is what the model tests, planned against the
    /// test file AS THE EDITOR HOLDS IT — its unsaved buffer — and nothing is written behind the editor.</summary>
    [Fact]
    public async Task TestsGenerate_TestsTheCodeSent_AgainstTheOpenBuffer_AndWritesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"tests-gen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "Calc.cs");
            File.WriteAllText(source, "class Calc { int Add(int a, int b) => a + b; int Sub(int a, int b) => a - b; }");
            var testPath = TestFilePathResolver.Resolve(source);
            using var h = CreateHarness();
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            await h.Client.NotifyWithParameterObjectAsync("textDocument/didOpen", new { path = testPath, text = "class CalcTests { /* unsaved */ }" });
            List<ChatMessageDto>? sent = null;
            h.Fake.OnChatRequest = (_, messages, _, _) =>
            {
                sent = messages;
                return Task.FromResult(new ChatTurnResult("class CalcTests { void Adds() { } }", null, 0, 0));
            };

            var result = await h.Client.InvokeWithParameterObjectAsync<Host.TestsGenerateResult>(
                    "tests/generate", new { path = source, code = "int Add(int a, int b) => a + b;" })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var user = sent!.Single(m => m.Role == "user").Content;
            Assert.Contains("int Add(int a, int b) => a + b;", user, StringComparison.Ordinal);
            Assert.DoesNotContain("Sub(", user, StringComparison.Ordinal);                   // the selection, not the file
            Assert.Contains("/* unsaved */", user, StringComparison.Ordinal);               // the buffer, not the disk
            Assert.True(result.Extended);
            Assert.Equal(testPath, result.TestPath);
            Assert.False(File.Exists(testPath));                                            // the editor writes it
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }
}
