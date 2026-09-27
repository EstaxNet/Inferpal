using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Services.Agent;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  One registry of model families, and what it says is true.
//
//  ModelProfiles tells the first-run choice which model to start with, the autocomplete which tokens to send, and
//  docs/models.md what to document. Each of those is a place where a family's facts were once written separately —
//  the first-run choice preferred a code model measured to complete 2 agent tasks in 8, and the autocomplete sent
//  fill-in-the-middle tokens to every model whose name contains "qwen". These tests hold the registry to the readers
//  that actually parse a reply, and to the page that documents it.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class ModelProfilesTests
{
    // ── Recognition ─────────────────────────────────────────────────────────

    [Theory]
    // The ids LM Studio serves them under…
    [InlineData("qwen/qwen3.8-27b",                    "qwen35")]
    [InlineData("qwen/qwen3.6-27b",                    "qwen35")]
    [InlineData("mistralai/devstral-small-2-2512",     "devstral")]
    [InlineData("prism-ml/bonsai-27b",                 "bonsai")]
    [InlineData("meta/muse-glimmer",                   "muse-glimmer")]
    [InlineData("google/gemma-4-31b-qat",              "gemma4")]
    [InlineData("qwen/qwen3-4b-thinking-2507",         "qwen3")]
    [InlineData("zai-org/glm-4.7-flash",               "glm47")]
    [InlineData("qwen2.5-coder-7b-instruct",           "qwen-coder")]
    // …and Ollama's.
    [InlineData("qwen2.5-coder:14b",                   "qwen-coder")]
    [InlineData("qwen3-coder:30b",                     "qwen3-coder")]
    [InlineData("qwen3:8b",                            "qwen3")]
    [InlineData("devstral:24b",                        "devstral")]
    [InlineData("gemma4:31b",                          "gemma4")]
    [InlineData("glm-4.7-flash:latest",                "glm47")]
    [InlineData("deepseek-coder-v2:16b",               "deepseek-coder")]
    [InlineData("starcoder2:7b",                       "starcoder")]
    [InlineData("CodeLlama:13b-instruct",              "codellama")]
    [InlineData("codegemma:7b",                        "codegemma")]
    public void AServedModelId_IsRecognisedAsItsFamily(string modelId, string anchor) =>
        Assert.Equal(anchor, ModelProfiles.For(modelId)?.Anchor);

    [Theory]
    [InlineData("llama3.1:8b")]
    [InlineData("mistral:7b")]
    [InlineData("gemma3:12b")]
    [InlineData("glm4:9b")]                                  // an older GLM, another call format: not the measured family
    [InlineData("text-embedding-qwen3-embedding-0.6b")]      // an embedding model is no chat family
    [InlineData("text-embedding-nomic-embed-text-v1.5")]
    [InlineData("")]
    [InlineData(null)]
    public void AModelOfNoProfiledFamily_IsNotGivenOne(string? modelId) =>
        Assert.Null(ModelProfiles.For(modelId));

    [Fact]
    public void NoProfile_IsShadowedByAnEarlierOne()
    {
        // The first match wins, so a profile placed after a broader one would never be reached: "qwen3" before
        // "qwen3-coder" would hand Qwen3 Coder the Qwen3 profile, without the autocomplete tokens it was trained on.
        foreach (var profile in ModelProfiles.All)
            foreach (var id in profile.Ids)
                Assert.True(ReferenceEquals(profile, ModelProfiles.For(id)),
                            $"'{id}' is read as {ModelProfiles.For(id)?.Anchor}, not {profile.Anchor}");
    }

    // ── Every form a profile declares has a reader ──────────────────────────

    /// <summary>A call to <c>read_file</c> with <c>path = src/a.cs</c>, in each form.</summary>
    private static readonly Dictionary<ToolCallForm, string> CallExamples = new()
    {
        [ToolCallForm.JsonTag]       = "<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"src/a.cs\"}}\n</tool_call>",
        [ToolCallForm.QwenXml]       = "<tool_call>\n<function=read_file>\n<parameter=path>\nsrc/a.cs\n</parameter>\n</function>\n</tool_call>",
        [ToolCallForm.GlmArgs]       = "<tool_call>read_file<arg_key>path</arg_key><arg_value>src/a.cs</arg_value></tool_call>",
        [ToolCallForm.MistralTokens] = "[TOOL_CALLS]read_file[ARGS]{\"path\": \"src/a.cs\"}",
        [ToolCallForm.GemmaNative]   = "<|tool_call>call:read_file{path:<|\"|>src/a.cs<|\"|>}<tool_call|>",
        [ToolCallForm.Atem]          = "<atem:function_calls>\n<atem:invoke name=\"read_file\">\n<atem:parameter name=\"path\">src/a.cs</atem:parameter>\n</atem:invoke>\n</atem:function_calls>",
    };

    [Fact]
    public void EveryToolCallForm_IsReadAsACall()
    {
        foreach (var form in Enum.GetValues<ToolCallForm>())
        {
            Assert.True(CallExamples.TryGetValue(form, out var text), $"{form} has no example: add one, and the reader it needs");

            var (calls, _) = InlineToolCallParser.TryParse(text!);

            var call = Assert.Single(calls!);
            Assert.True(call.Function.Name == "read_file", $"{form}: read as '{call.Function.Name}'");
            Assert.Equal("src/a.cs", call.Function.Arguments.GetProperty("path").GetString());
        }
    }

    /// <summary>What the answer reads as once the reasoning, written the family's way, is taken out.</summary>
    private static readonly Dictionary<ReasoningForm, Func<string>> ReasoningExamples = new()
    {
        [ReasoningForm.None]                = () => MarkdownParser.StripThinkTags("The answer."),
        [ReasoningForm.TemplateOpenedThink] = () => MarkdownParser.StripThinkTags("The user wants a sentence.\n</think>\n\nThe answer."),
        [ReasoningForm.ChannelEnvelope]     = () =>
        {
            var envelope = new ChannelEnvelope();
            var answer   = new StringBuilder(envelope.Push(" to=self<|message|>The user wants a sentence.<|eom|><|start|>assistant to=user<|message|>The answer.").Answer);
            return answer.Append(envelope.Flush().Answer).ToString();
        },
    };

    [Fact]
    public void EveryReasoningForm_LeavesTheAnswerOnly()
    {
        foreach (var form in Enum.GetValues<ReasoningForm>())
        {
            Assert.True(ReasoningExamples.TryGetValue(form, out var read), $"{form} has no example: add one, and the reader it needs");

            var answer = read!().Trim();
            Assert.True(answer == "The answer.", $"{form}: the answer reads '{answer}'");
        }
    }

    // ── The documentation ───────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Anchors of the page that name no family.</summary>
    private static readonly string[] SectionAnchors = ["fim-models"];

    [Fact]
    public void EveryFamily_IsDocumented_AndTheDocumentationNamesNoOtherFamily()
    {
        var page    = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "models.md"));
        var anchors = Regex.Matches(page, "<a id=\"([^\"]+)\"></a>").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.True(anchors.Count >= ModelProfiles.All.Count, $"only {anchors.Count} anchors read");   // witness: the page is read

        var profiles = ModelProfiles.All.Select(p => p.Anchor).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(profiles.Except(anchors));
        Assert.Empty(anchors.Except(profiles).Except(SectionAnchors));
    }

    [Fact]
    public void EveryFamilyTrainedToFillInTheMiddle_IsInTheAutocompleteTable()
    {
        var page  = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "models.md"));
        var start = page.IndexOf("<a id=\"fim-models\">", StringComparison.Ordinal);
        Assert.True(start >= 0, "the autocomplete section is gone");
        var heading = page.IndexOf("\n## ", start, StringComparison.Ordinal);
        var section = page[start..page.IndexOf("\n## ", heading + 1, StringComparison.Ordinal)];
        Assert.Contains("| Family |", section);   // witness: the table itself is read, not just its heading

        foreach (var profile in ModelProfiles.All.Where(p => p.Fim != FimForm.None))
            Assert.True(section.Contains($"(#{profile.Anchor})", StringComparison.Ordinal)
                     || section.Contains($"<a id=\"{profile.Anchor}\">", StringComparison.Ordinal),
                        $"{profile.Name} completes with fill-in-the-middle tokens and the table does not say so");
    }

    // ── The decisions the profiles feed ─────────────────────────────────────

    [Theory]
    [InlineData("qwen/qwen3.8-27b")]
    [InlineData("qwen/qwen3.6-27b")]
    [InlineData("qwen/qwen3-4b-thinking-2507")]
    [InlineData("prism-ml/bonsai-27b")]
    public void AQwenChatModel_CompletesFromThePrefix_WithoutTokensItWasNotTrainedOn(string model)
    {
        var spec = FimTemplate.Build(model, "PRE", "SUF");

        Assert.False(spec.IsFim);
        Assert.Equal("PRE", spec.Prompt);
    }

    [Fact]
    public void EveryFamilyTrainedToFillInTheMiddle_GetsItsTokens()
    {
        foreach (var profile in ModelProfiles.All)
            Assert.Equal(profile.Fim != FimForm.None, FimTemplate.Build(profile.Ids[0], "PRE", "SUF").IsFim);
    }
}
