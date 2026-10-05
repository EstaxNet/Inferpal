using System;
using System.IO;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  With no tool offered, a tool call SHOWN in a reply is content, not a call.
//
//  Both chat clients recovered tool calls written as text whether or not the request offered any
//  tool. An inline edit, /doc or /test on a file that holds a tool-call literal — an agent's prompt
//  template, a test of the parser — came back without those lines: the client had cut them out as a
//  call, and the edit wrote the rest over the user's code. A reply that is nothing but a call is
//  still read as one, and a request that offered tools recovers calls as before.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class ShownToolCallIsContentTests
{
    private const string PromptTemplate =
        "SYSTEM_PROMPT = '''\n" +
        "To read a file, answer exactly:\n" +
        "<tool_call>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}</tool_call>\n" +
        "'''\n";

    [Fact]
    public void NoToolOffered_ACallShownInCode_StaysInTheReply()
    {
        // Witness: the parser does read a call in it — otherwise this test measures nothing.
        Assert.NotNull(InlineToolCallParser.TryParse(PromptTemplate).Calls);

        var (calls, text) = InlineToolCallParser.FromContent(PromptTemplate, Array.Empty<string>());

        Assert.Null(calls);
        Assert.Equal(PromptTemplate, text);
    }

    [Fact]
    public void NoToolOffered_AReplyThatIsOnlyACall_IsStillACall()
    {
        // Reference arm: asked for an answer, the model wrote none — its readers refuse an empty reply, as before.
        var (calls, text) = InlineToolCallParser.FromContent(
            "<tool_call>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}</tool_call>", Array.Empty<string>());

        Assert.Single(calls!);
        Assert.Equal(string.Empty, text.Trim());
    }

    [Fact]
    public void ToolsOffered_ACallBesideProse_IsRecoveredAsBefore()
    {
        var (calls, _) = InlineToolCallParser.FromContent(
            "Let me read it.\n<tool_call>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}</tool_call>",
            new[] { "read_file" });

        Assert.Single(calls!);
    }

    [Theory]
    [InlineData("OllamaClient.cs")]
    [InlineData("OpenAiCompatibleClient.cs")]
    public void BothClients_DecideThroughTheOneReader(string client)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(dir!.FullName, "Inferpal.Core", "Services", "Inference", client));

        Assert.Contains("InlineToolCallParser.FromContent(", code, StringComparison.Ordinal);
    }
}
