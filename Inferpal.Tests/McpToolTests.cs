using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

public class McpToolTests
{
    [Fact]
    public void BuildName_NamespacesServerAndTool()
    {
        Assert.Equal("mcp__filesystem__read_file", McpTool.BuildName("filesystem", "read_file"));
    }

    [Theory]
    // Anything outside [a-zA-Z0-9_] becomes '_' to satisfy the Ollama tool-name constraint.
    [InlineData("my-server", "list.dir",  "mcp__my_server__list_dir")]
    [InlineData("git hub",   "get/issue", "mcp__git_hub__get_issue")]
    [InlineData("srv",       "café",      "mcp__srv__caf_")]
    public void BuildName_SanitizesIllegalCharacters(string server, string tool, string expected)
    {
        Assert.Equal(expected, McpTool.BuildName(server, tool));
    }

    [Fact]
    public void BuildName_KeepsDigitsAndUnderscores()
    {
        Assert.Equal("mcp__srv_1__tool_2", McpTool.BuildName("srv_1", "tool_2"));
    }
}
