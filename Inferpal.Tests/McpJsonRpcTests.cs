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

    /// <summary>
    /// A third-party server can list an entry that is not a named object. <c>TryGetProperty</c> throws on a non-object
    /// and <c>GetString</c> on a non-string: the listing failed as a whole, and every tool of the server was lost for
    /// the one bad entry — where the contract says unnamed entries are skipped.
    /// </summary>
    [Fact]
    public void ParseTools_SkipsEntriesThatAreNotNamedObjects_AndKeepsTheRest()
    {
        var tools = McpJsonRpc.ParseTools(El("""
        { "tools": [ null, "read", { "name": 42 }, { "name": "ok", "description": 7 } ] }
        """));

        var t = Assert.Single(tools);
        Assert.Equal("ok", t.Name);
        Assert.Equal(string.Empty, t.Description);
    }

    [Fact]
    public void ParseTools_AResultThatIsNotAnObject_ReturnsEmpty()
    {
        Assert.Empty(McpJsonRpc.ParseTools(El("null")));
        Assert.Empty(McpJsonRpc.ParseTools(El("[]")));
    }

    /// <summary>One malformed content block made the whole call fail: the text the other blocks carried was lost.</summary>
    [Fact]
    public void ExtractCallResult_SkipsMalformedBlocks_AndKeepsTheText()
    {
        var text = McpJsonRpc.ExtractCallResult(El("""
        { "content": [
            null,
            { "type": 1 },
            { "type": "text", "text": 5 },
            { "type": "resource", "resource": "not an object" },
            { "type": "text", "text": "kept" }
        ] }
        """), "t");

        Assert.Equal("kept", text);
    }

    /// <summary>
    /// Not every server sends <c>error</c> as <c>{ "message": … }</c>: a bare string threw on <c>TryGetProperty</c>,
    /// and the server's own words were replaced by a .NET message.
    /// </summary>
    [Fact]
    public void ErrorMessage_ReadsTheObjectForm_AndTheBareStringForm()
    {
        Assert.Equal("boom", McpJsonRpc.ErrorMessage(El("""{ "code": -32000, "message": "boom" }""")));
        Assert.Equal("boom", McpJsonRpc.ErrorMessage(El("\"boom\"")));
        Assert.Equal("unknown error", McpJsonRpc.ErrorMessage(El("""{ "message": 42 }""")));
        Assert.Equal("unknown error", McpJsonRpc.ErrorMessage(El("null")));
    }

    /// <summary>The two transports read the error the same way: they held two identical copies of the throwing read.</summary>
    [Theory]
    [InlineData("McpStdioClient.cs")]
    [InlineData("McpHttpClient.cs")]
    public void BothTransports_ReadTheErrorThroughTheSharedReader(string file)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var code = ConventionCoverageTests.CodeOnly(
            System.IO.Path.Combine(dir!.FullName, "Inferpal.Core", "Services", "Mcp", file));

        Assert.Contains("McpJsonRpc.ErrorMessage(", code);
        Assert.DoesNotContain("TryGetProperty(\"message\"", code);
    }

    [Fact]
    public void ExtractCallResult_AResultThatIsNotAnObject_ReturnsPlaceholder()
    {
        Assert.Equal("(no output)", McpJsonRpc.ExtractCallResult(El("null"), "t"));
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
