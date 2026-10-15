using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Inferpal.Services.Execution;
using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The advice the model reads after a call of an MCP server's tool failed agrees with the cause.
/// </summary>
/// <remarks>
/// MCP calls went through the advice written for the built-in tools, which reads the root exception's type: a server
/// that refused the arguments (-32602) got "the arguments are not the cause", an answer that was not JSON got "check the
/// arguments against the tool's schema", and a connection cut mid-answer (an IOException) got "this file or folder cannot
/// be accessed".
/// </remarks>
public sealed class McpToolFailureAdviceTests
{
    private const string Tool = "mcp__docs__search";

    private static JsonElement RpcError(long code, string message) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { code, message })).RootElement.Clone();

    [Fact]
    public void ArgumentsTheServerRefused_AreTheOnesToCheck()
    {
        var said = ToolFailure.Describe(Tool, new McpRpcException(RpcError(-32602, "Invalid params: 'query' is required")));

        Assert.Contains("'query' is required", said);
        Assert.Contains("refused these arguments", said);
        Assert.DoesNotContain("not the cause", said);
    }

    [Fact]
    public void ArgumentsRefusedInAnHttpError_AreTheOnesToCheck()
    {
        var said = ToolFailure.Describe(Tool, new McpHttpRefusedException(
            "HTTP 400: Invalid params", HttpStatusCode.BadRequest, -32602, null, "Invalid params"));

        Assert.Contains("refused these arguments", said);
    }

    [Fact]
    public void AnAnswerThatIsNotJson_IsNotTheArguments()
    {
        var said = ToolFailure.Describe(Tool, new JsonException("'<' is an invalid start of a value."));

        Assert.DoesNotContain("Check the arguments", said);
        Assert.Contains("the MCP server, or the connection to it", said);
    }

    [Fact]
    public void AConnectionCutMidAnswer_IsNotAFile()
    {
        // What reading an answer stream the server closed early throws: an IOException, not wrapped.
        var said = ToolFailure.Describe(Tool, new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

        Assert.DoesNotContain("file or folder", said);
        Assert.Contains("the MCP server, or the connection to it", said);
        Assert.Contains("The response ended prematurely", said);
    }

    [Fact]
    public void AnotherServerError_SaysTheArgumentsAreNotTheCause()
    {
        var said = ToolFailure.Describe(Tool, new McpRpcException(RpcError(-32603, "Internal error")));

        Assert.Contains("The arguments are not the cause", said);
    }

    [Fact]
    public void TheBuiltInTools_KeepTheirAdvice()
    {
        // Reference arm: the type hierarchy still decides for a tool of this product.
        Assert.Contains("Check the arguments", ToolFailure.Describe("read_file", new ArgumentException("path is required")));
        Assert.Contains("file or folder", ToolFailure.Describe("write_file", new IOException("The process cannot access the file.")));
    }
}
