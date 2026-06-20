using System.Text.Json;
using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

public class McpJsonRpcTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParseTools_ReadsNameDescriptionSchema()
    {
        var tools = McpJsonRpc.ParseTools(El("""
        { "tools": [ { "name": "read", "description": "reads", "inputSchema": { "type": "object" } } ] }
        """));

        var t = Assert.Single(tools);
        Assert.Equal("read", t.Name);
        Assert.Equal("reads", t.Description);
        Assert.Equal(JsonValueKind.Object, t.InputSchema.ValueKind);
        Assert.Equal("object", t.InputSchema.GetProperty("type").GetString());
    }

    [Fact]
    public void ParseTools_SkipsUnnamed_AndDefaultsMissingSchemaAndDescription()
    {
        var tools = McpJsonRpc.ParseTools(El("""
        { "tools": [ { "description": "no name" }, { "name": "ok" } ] }
        """));

        var t = Assert.Single(tools);
        Assert.Equal("ok", t.Name);
        Assert.Equal(string.Empty, t.Description);
        Assert.Equal(JsonValueKind.Object, t.InputSchema.ValueKind);   // {} fallback
    }

    [Fact]
    public void ParseTools_NoToolsArray_ReturnsEmpty()
    {
        Assert.Empty(McpJsonRpc.ParseTools(El("{}")));
        Assert.Empty(McpJsonRpc.ParseTools(El("""{ "tools": "nope" }""")));
    }

    [Fact]
    public void ExtractCallResult_ConcatenatesTextAndResourceBlocks()
    {
        var text = McpJsonRpc.ExtractCallResult(El("""
        { "content": [
            { "type": "text", "text": "line one" },
            { "type": "resource", "resource": { "text": "line two" } },
            { "type": "image", "data": "ignored" }
        ] }
        """), "t");

        Assert.Equal("line one\nline two", text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ExtractCallResult_EmptyContent_ReturnsPlaceholder()
    {
        Assert.Equal("(no output)", McpJsonRpc.ExtractCallResult(El("""{ "content": [] }"""), "t"));
        Assert.Equal("(no output)", McpJsonRpc.ExtractCallResult(El("{}"), "t"));
    }

    [Fact]
    public void ExtractCallResult_IsError_WrapsMessage()
    {
        var text = McpJsonRpc.ExtractCallResult(El("""
        { "isError": true, "content": [ { "type": "text", "text": "boom" } ] }
        """), "danger");

        Assert.Equal("MCP tool 'danger' reported an error: boom", text);
    }
}
