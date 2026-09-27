using System.Text.Json;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  Every tool-call format a supported model writes as text is read as a call.
//
//  A server parses a model's calls into structured tool_calls only when it knows that model's format — LM Studio
//  parses Muse Glimmer's ATEM calls only when a call is forced, and a server without a parser for the model leaves
//  them all as text. The examples below are the vendors' own (model cards, chat templates, function-calling guides);
//  docs/models.md lists which model writes which.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class ModelToolCallFormatsTests
{
    private static (string Name, JsonElement Args) Single(string text)
    {
        var (calls, cleaned) = InlineToolCallParser.TryParse(text);
        var call = Assert.Single(calls!);
        Assert.Null(call.Function.UnparsedArguments);
        Assert.DoesNotContain("tool_call", cleaned);   // nothing of the call is left to show
        return (call.Function.Name, call.Function.Arguments);
    }

    [Theory]
    // Qwen3 / Hermes JSON (Qwen3 4B Thinking, Qwen2.5 Coder)
    [InlineData("<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"src/a.cs\"}}\n</tool_call>")]
    // Qwen3.6 / 3.8 / Bonsai XML
    [InlineData("<tool_call>\n<function=read_file>\n<parameter=path>\nsrc/a.cs\n</parameter>\n</function>\n</tool_call>")]
    // Muse Glimmer ATEM
    [InlineData("<atem:function_calls>\n<atem:invoke name=\"read_file\">\n<atem:parameter name=\"path\">src/a.cs</atem:parameter>\n</atem:invoke>\n</atem:function_calls>")]
    // GLM 4.7 (no newline after the name) and GLM 4.5 / 4.6 (a newline)
    [InlineData("<tool_call>read_file<arg_key>path</arg_key><arg_value>src/a.cs</arg_value></tool_call>")]
    [InlineData("<tool_call>read_file\n<arg_key>path</arg_key>\n<arg_value>src/a.cs</arg_value>\n</tool_call>")]
    // Gemma 4
    [InlineData("<|tool_call>call:read_file{path:<|\"|>src/a.cs<|\"|>}<tool_call|>")]
    // Mistral / Devstral, current and older forms
    [InlineData("[TOOL_CALLS]read_file[ARGS]{\"path\": \"src/a.cs\"}")]
    [InlineData("[TOOL_CALLS][{\"name\": \"read_file\", \"arguments\": {\"path\": \"src/a.cs\"}}]")]
    public void EveryDocumentedFormat_IsReadAsTheSameCall(string text)
    {
        var (name, args) = Single(text);

        Assert.Equal("read_file", name);
        Assert.Equal("src/a.cs", args.GetProperty("path").GetString());
    }

    [Fact]
    public void GemmaArguments_KeepTheirTypes_AndStringsKeepTheirPunctuation()
    {
        var (name, args) = Single("<|tool_call>call:search{query:<|\"|>a, b: {c}<|\"|>,top_k:5,exact:true,paths:[<|\"|>src<|\"|>,<|\"|>tests<|\"|>],opts:{deep:false}}<tool_call|>");

        Assert.Equal("search", name);
        Assert.Equal("a, b: {c}", args.GetProperty("query").GetString());
        Assert.Equal(5, args.GetProperty("top_k").GetInt32());
        Assert.True(args.GetProperty("exact").GetBoolean());
        Assert.Equal(["src", "tests"], args.GetProperty("paths").EnumerateArray().Select(e => e.GetString()));
        Assert.False(args.GetProperty("opts").GetProperty("deep").GetBoolean());
    }

    [Fact]
    public void AGemmaCallInAnotherSyntax_IsUnreadable_NotACallWithNoArguments()
    {
        // Named, never run with defaults: ExecuteToolSafeAsync refuses a call whose arguments could not be read.
        var (calls, _) = InlineToolCallParser.TryParse("<|tool_call>call:run_tests{filter:<|\"|>Pricing}<tool_call|>");

        var call = Assert.Single(calls!);
        Assert.Equal("run_tests", call.Function.Name);
        Assert.NotNull(call.Function.UnparsedArguments);
    }

    [Fact]
    public void ParallelMistralCalls_AreAllRead()
    {
        var (calls, cleaned) = InlineToolCallParser.TryParse(
            "[TOOL_CALLS]read_file[ARGS]{\"path\": \"a.cs\"}[TOOL_CALLS]read_file[ARGS]{\"path\": \"b.cs \\\"}\\\" .cs\"}");

        Assert.Equal(["a.cs", "b.cs \"}\" .cs"], calls!.Select(c => c.Function.Arguments.GetProperty("path").GetString()));
        Assert.Equal(string.Empty, cleaned);
    }

    [Theory]
    [InlineData("Mistral writes its calls as [TOOL_CALLS] followed by the name.")]
    [InlineData("GLM wraps calls in <tool_call> tags; see the model card.")]
    [InlineData("The token <|tool_call> opens a Gemma call.")]
    public void ProseThatNamesAFormat_IsNotACall(string text)
    {
        // Reference arm: a format named in an answer is not a call written in it.
        var (calls, cleaned) = InlineToolCallParser.TryParse(text);

        Assert.Null(calls);
        Assert.Equal(text, cleaned);
    }
}
