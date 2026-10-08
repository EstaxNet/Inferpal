using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Docs;
using Inferpal.Services.Inference;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A value written as TEXT in a tool call takes its type from the tool's schema, never from its look.
//
//  Qwen's <parameter=k>value</parameter>, GLM's <arg_value> and ATEM write every value as text. The
//  parser guessed the type from the first character: write_file of a package.json received an object
//  for "content", apply_diff from false to true two booleans — the tools read strings, found none, and
//  answered that the argument is required. Qwen 3 on LM Studio writes this shape in its reasoning when
//  a call is forced; both clients recover it there and from the content. The schemas used here are the
//  real ones, from the registry the host builds.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class TextCallValueSchemaTests
{
    private static IReadOnlyList<ToolDefinition> RealDefinitions()
    {
        var config   = new InferpalConfig();
        var client   = new FakeInferenceProvider();
        var editor   = new NullEditorSurface();
        var approval = new NoopApproval();
        var index    = new ProjectIndexService(client, config, new LspSemanticProvider());
        var registry = new ToolRegistry(editor, approval, config, index, client,
                                        new ProjectMapService(editor), new McpToolService(config, approval),
                                        new DocsIndexService(client, config), new OpenDocumentOverlay(),
                                        new NullDebugSession());
        var defs = registry.Definitions;
        Assert.Contains(defs, d => d.Function.Name == "write_file");   // witness: the real schemas are read
        return defs;
    }

    private static readonly Func<string, JsonElement?> SchemaOf = ToolSchemas.Of(RealDefinitions());

    private static JsonElement Args(string content)
    {
        var (calls, _) = InlineToolCallParser.TryParse(content, schemaOf: SchemaOf);
        return Assert.Single(calls!).Function.Arguments;
    }

    private static string Qwen(string tool, params (string Key, string Value)[] ps) =>
        $"<tool_call>\n<function={tool}>\n"
        + string.Concat(ps.Select(p => $"<parameter={p.Key}>\n{p.Value}\n</parameter>\n"))
        + "</function>\n</tool_call>";

    private const string PackageJson = "{\n  \"name\": \"shop\",\n  \"version\": \"1.0.0\"\n}\n";

    [Fact]
    public void AJsonDocumentWrittenByWriteFile_IsItsContent_Verbatim()
    {
        var args = Args(Qwen("write_file", ("path", "package.json"), ("content", PackageJson)));

        Assert.Equal(JsonValueKind.String, args.GetProperty("content").ValueKind);
        Assert.Equal(PackageJson, args.GetProperty("content").GetString());   // final line break kept
        Assert.Equal("package.json", args.GetProperty("path").GetString());
    }

    [Fact]
    public void AnEditFromFalseToTrue_KeepsBothSidesAsText()
    {
        var args = Args(Qwen("apply_diff", ("path", "a.cs"), ("old_content", "false"), ("new_content", "true")));

        Assert.Equal("false", args.GetProperty("old_content").GetString());
        Assert.Equal("true", args.GetProperty("new_content").GetString());
    }

    [Fact]
    public void ABlockOfCode_KeepsTheIndentationOfItsFirstLine()
    {
        const string old = "    if (total > 0)\n        return total;";
        var args = Args(Qwen("apply_diff", ("path", "a.cs"), ("old_content", old), ("new_content", "    return 0;")));

        Assert.Equal(old, args.GetProperty("old_content").GetString());
        Assert.Equal("return 0;", args.GetProperty("new_content").GetString());   // one line: its spaces are layout
    }

    [Fact]
    public void ParametersTheSchemaDeclaresAsNumbersBooleansOrArrays_AreTyped()
    {
        Assert.Equal(JsonValueKind.Number,
            Args(Qwen("read_file", ("path", "a.cs"), ("start_line", "12"))).GetProperty("start_line").ValueKind);
        Assert.Equal(JsonValueKind.False,
            Args(Qwen("rename_symbol", ("symbol", "A"), ("new_name", "B"), ("dry_run", "False")))
                .GetProperty("dry_run").ValueKind);
        var edits = Args(Qwen("apply_edits",
            ("edits", "[{\"path\":\"a.cs\",\"old_content\":\"1\",\"new_content\":\"2\"}]"))).GetProperty("edits");
        Assert.Equal(JsonValueKind.Array, edits.ValueKind);
        Assert.Equal("1", edits[0].GetProperty("old_content").GetString());
    }

    [Fact]
    public void GlmAndAtemValues_FollowTheSchemaToo()
    {
        var glm = Args("<tool_call>write_file<arg_key>path</arg_key><arg_value>n.txt</arg_value>"
                       + "<arg_key>content</arg_key><arg_value>42</arg_value></tool_call>");
        Assert.Equal("42", glm.GetProperty("content").GetString());

        var atem = Args("<atem:function_calls><atem:invoke name=\"write_file\">"
                        + "<atem:parameter name=\"path\">n.json</atem:parameter>"
                        + "<atem:parameter name=\"content\">[1, 2]</atem:parameter>"
                        + "</atem:invoke></atem:function_calls>");
        Assert.Equal("[1, 2]", atem.GetProperty("content").GetString());
    }

    [Fact]
    public void ATypedValueThatIsNotOfItsType_StaysTextForTheToolToName()
    {
        // "twelve" is not a number: the tool reads the text and says what it expected, instead of an empty value.
        var args = Args(Qwen("read_file", ("path", "a.cs"), ("start_line", "twelve")));
        Assert.Equal("twelve", args.GetProperty("start_line").GetString());
    }

    [Fact]
    public void WithoutASchema_TheTypeIsStillGuessed()
    {
        // Reference arm: a tool nobody offered keeps the old reading (the registry refuses it anyway).
        var (calls, _) = InlineToolCallParser.TryParse(Qwen("search", ("top_k", "5")), schemaOf: SchemaOf);
        Assert.Equal(JsonValueKind.Number, Assert.Single(calls!).Function.Arguments.GetProperty("top_k").ValueKind);
    }

    // ── The production clients, a real socket: the call recovered from the reasoning ──

    private sealed class Tools(IReadOnlyList<ToolDefinition> defs) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = defs;
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("ok");
    }

    private static readonly string Call = Qwen("write_file", ("path", "package.json"), ("content", PackageJson));

    [Fact]
    public async Task OpenAiCompatible_ACallInTheReasoning_KeepsItsJsonContentAsText()
    {
        var body = "data: "
                   + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { reasoning_content = Call } } } })
                   + "\n\ndata: [DONE]\n\n";
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? body : null);
        var client = new OpenAiCompatibleClient(new InferpalConfig
        {
            Provider = "openai-compatible", BaseUrl = server.BaseUrl, ContextWindowSize = 262_144,
        });

        var result = await client.SendChatAsync("m", [new ChatMessageDto("user", "create package.json")],
                                                new Tools(RealDefinitions()), onToken: null, CancellationToken.None);

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal(PackageJson, call.Function.Arguments.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Ollama_ACallInTheThinking_KeepsItsJsonContentAsText()
    {
        var ndjson = "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"thinking\":" + JsonSerializer.Serialize(Call)
                     + "},\"done\":false}\n"
                     + "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\","
                     + "\"eval_count\":40,\"prompt_eval_count\":60}\n";
        using var server = new LoopbackHttpServer(path => path.StartsWith("/api/chat", StringComparison.Ordinal) ? ndjson : null);

        var result = await new OllamaClient(new InferpalConfig { BaseUrl = server.BaseUrl, ContextWindowSize = 262_144 })
            .SendChatAsync("m", [new ChatMessageDto("user", "create package.json")], new Tools(RealDefinitions()),
                           onToken: null, CancellationToken.None);

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal(PackageJson, call.Function.Arguments.GetProperty("content").GetString());
    }
}
