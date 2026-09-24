using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ An in-place action (/fix, /refactor, /doc, "Edit with AI") replaces the code with the model's
/// answer — and an answer that stopped at the length limit (the window filled up during the rewrite:
/// the whole file in, the whole file expected out) is the file's FIRST part. The server says so
/// (<c>finish_reason: "length"</c>, <c>done_reason: "length"</c>), the clients read it for a diagnostic
/// line and nobody else: the cut rewrite was applied as a success, and the end of the file went with it.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares a localized sentence
public class CutReplyTests
{
    private const string Document =
        "public static class Pricing\n{\n"
        + "    public static int One() => 1;\n"
        + "    public static int Two() => 2;\n"
        + "    public static int Three() => 3;\n"
        + "}\n";

    // The rewrite as it comes back when the window fills up: an opening fence, the first method, no end.
    private const string CutRewrite = "```csharp\n/// <summary>Pricing.</summary>\npublic static class Pricing\n{\n    /// <summary>One.</summary>\n    public static int One() => 1;\n";

    private static string OpenAiStream(string finishReason) =>
        "data: " + System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = CutRewrite } } } }) + "\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"" + finishReason + "\"}]}\n\n"
        + "data: [DONE]\n\n";

    private static Task<CodeActionRun> RefactorWholeFileAsync(IInferenceProvider client) =>
        CodeActionPipeline.RunAsync(client, "m", "system", "Add doc comments.", Document,
                                    selStart: 0, selEnd: 0, selectionEmpty: true, CancellationToken.None);

    [Fact]
    public async Task ARewriteCutAtTheLengthLimit_IsNotApplied_AndSaysWhy()
    {
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? OpenAiStream("length") : null);
        var run = await RefactorWholeFileAsync(
            new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl }));

        Assert.Contains("/v1/chat/completions", string.Join(" ", server.Paths));   // witness: the model answered
        Assert.NotEqual(CodeActionOutcome.Edited, run.Outcome);                     // the end of the file stays
        Assert.Null(run.NewDocText);
        Assert.Equal(Strings.CodeActionReplyCut, run.FailureDetail);
    }

    [Fact]
    public async Task TheSameRewriteFinishedNormally_IsApplied()
    {
        // Reference arm: a rewrite that ends on "stop" is what the model meant to write.
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? OpenAiStream("stop") : null);
        var run = await RefactorWholeFileAsync(
            new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl }));

        Assert.Equal(CodeActionOutcome.Edited, run.Outcome);
    }

    [Fact]
    public async Task Ollama_ReportsTheCutToo()
    {
        var content = System.Text.Json.JsonSerializer.Serialize(CutRewrite);
        var ndjson  = "{\"message\":{\"role\":\"assistant\",\"content\":" + content + "},\"done\":false}\n"
                    + "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"length\",\"eval_count\":40,\"prompt_eval_count\":60}\n";
        using var server = new LoopbackHttpServer(path => path.StartsWith("/api/chat", StringComparison.Ordinal) ? ndjson : null);

        var turn = await new OllamaClient(new InferpalConfig { BaseUrl = server.BaseUrl }).SendChatAsync(
            "m", [new ChatMessageDto("user", "rewrite")], EmptyToolRegistry.Instance, onToken: null, CancellationToken.None);

        Assert.Contains("/api/chat", string.Join(" ", server.Paths));
        Assert.True(turn.CutAtLimit);
    }

    [Fact]
    public void TheFunnel_RefusesACutReply_WhateverItsShape()
    {
        // "Edit with AI" calls the funnel directly: the verdict must not depend on which caller ran the model.
        var run = CodeActionPipeline.Finish(CutRewrite, Document, Document, reindent: false, "m", "server", cutAtLimit: true);

        Assert.Equal(CodeActionOutcome.Failed, run.Outcome);
        Assert.Equal(Strings.CodeActionReplyCut, run.FailureDetail);
    }
}
