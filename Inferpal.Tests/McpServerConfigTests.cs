using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

public class McpServerConfigTests
{
    [Fact]
    public void Parse_DirectMap_ReadsCommandArgsEnv()
    {
        const string json = """
        {
          "filesystem": {
            "command": "npx",
            "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\dev"],
            "env": { "FOO": "bar" }
          }
        }
        """;

        var servers = McpServerConfig.Parse(json);

        var s = Assert.Single(servers);
        Assert.Equal("filesystem", s.Name);
        Assert.Equal("npx", s.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "C:\\dev"], s.Args);
        Assert.Equal("bar", s.Env["FOO"]);
        Assert.True(s.Enabled);
    }

    [Fact]
    public void Parse_AcceptsMcpServersWrapper()
    {
        // Claude Desktop / Continue configs nest under "mcpServers".
        const string json = """
        { "mcpServers": { "git": { "command": "uvx", "args": ["mcp-server-git"] } } }
        """;

        var s = Assert.Single(McpServerConfig.Parse(json));
        Assert.Equal("git", s.Name);
        Assert.Equal("uvx", s.Command);
        Assert.Equal(["mcp-server-git"], s.Args);
    }

    [Fact]
    public void Parse_DisabledFlag_MarksServerDisabled()
    {
        const string json = """
        {
          "on":  { "command": "a" },
          "off": { "command": "b", "disabled": true }
        }
        """;

        var servers = McpServerConfig.Parse(json);

        Assert.True(servers.Single(s => s.Name == "on").Enabled);
        Assert.False(servers.Single(s => s.Name == "off").Enabled);
    }

    [Fact]
    public void Parse_SkipsEntriesWithoutCommand()
    {
        const string json = """
        {
          "good": { "command": "ok" },
          "bad":  { "args": ["x"] },
          "blank": { "command": "   " }
        }
        """;

        var s = Assert.Single(McpServerConfig.Parse(json));
        Assert.Equal("good", s.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]      // valid JSON, wrong shape
    [InlineData("42")]
    public void Parse_EmptyOrMalformed_ReturnsEmpty(string? json)
    {
        Assert.Empty(McpServerConfig.Parse(json));
    }

    [Fact]
    public void Parse_IgnoresNonStringArgsAndEnvValues()
    {
        const string json = """
        {
          "s": { "command": "c", "args": ["keep", 7, true], "env": { "A": "1", "B": 2 } }
        }
        """;

        var s = Assert.Single(McpServerConfig.Parse(json));
        Assert.Equal(["keep"], s.Args);
        Assert.Equal("1", s.Env["A"]);
        Assert.False(s.Env.ContainsKey("B"));
    }

    [Fact]
    public void Serialize_RoundTrips_ThroughParse()
    {
        IReadOnlyList<McpServerConfig> original =
        [
            new("filesystem", "npx", ["-y", "server-fs"], new Dictionary<string, string> { ["K"] = "V" }),
            new("disabled-one", "cmd", [], new Dictionary<string, string>(), Enabled: false),
        ];

        var reparsed = McpServerConfig.Parse(McpServerConfig.Serialize(original));

        var fs = reparsed.Single(s => s.Name == "filesystem");
        Assert.Equal("npx", fs.Command);
        Assert.Equal(["-y", "server-fs"], fs.Args);
        Assert.Equal("V", fs.Env["K"]);
        Assert.True(fs.Enabled);

        Assert.False(reparsed.Single(s => s.Name == "disabled-one").Enabled);
    }

    [Fact]
    public void Serialize_OmitsEmptyArgsEnv_AndDisabledWhenEnabled()
    {
        IReadOnlyList<McpServerConfig> servers =
            [new("bare", "go", [], new Dictionary<string, string>())];

        var json = McpServerConfig.Serialize(servers);

        Assert.Contains("\"command\"", json);
        Assert.DoesNotContain("\"args\"", json);
        Assert.DoesNotContain("\"env\"", json);
        Assert.DoesNotContain("\"disabled\"", json);
    }

    [Fact]
    public void Parse_HttpServer_ReadsUrlAndHeaders()
    {
        const string json = """
        {
          "remote": {
            "url": "https://mcp.example.com/mcp",
            "headers": { "Authorization": "Bearer ${TOK}" }
          }
        }
        """;

        var s = Assert.Single(McpServerConfig.Parse(json));
        Assert.True(s.IsHttp);
        Assert.Equal("https://mcp.example.com/mcp", s.Url);
        Assert.Equal("Bearer ${TOK}", s.Headers!["Authorization"]);
        Assert.Null(s.Command);
    }

    [Fact]
    public void Parse_StdioServer_IsNotHttp()
    {
        var s = Assert.Single(McpServerConfig.Parse("""{ "fs": { "command": "npx" } }"""));
        Assert.False(s.IsHttp);
        Assert.Null(s.Url);
    }

    [Fact]
    public void Parse_MixedStdioAndHttp()
    {
        const string json = """
        {
          "local":  { "command": "go" },
          "remote": { "url": "https://x/mcp" }
        }
        """;

        var servers = McpServerConfig.Parse(json);

        Assert.False(servers.Single(s => s.Name == "local").IsHttp);
        Assert.True(servers.Single(s => s.Name == "remote").IsHttp);
    }

    [Fact]
    public void Parse_HttpServer_ReadsOAuthBlock()
    {
        const string json = """
        {
          "remote": {
            "url": "https://mcp.example.com/mcp",
            "oauth": { "client_id": "cid", "client_secret": "sek", "scopes": ["a", "b"] }
          }
        }
        """;

        var s = Assert.Single(McpServerConfig.Parse(json));
        Assert.NotNull(s.OAuth);
        Assert.Equal("cid", s.OAuth!.ClientId);
        Assert.Equal("sek", s.OAuth.ClientSecret);
        Assert.Equal(["a", "b"], s.OAuth.Scopes);
    }

    [Fact]
    public void Parse_HttpServer_NoOAuthBlock_IsNull()
    {
        var s = Assert.Single(McpServerConfig.Parse("""{ "remote": { "url": "https://x/mcp" } }"""));
        Assert.Null(s.OAuth);
    }

    [Fact]
    public void Serialize_OAuth_RoundTrips_AndOmitsEmptyFields()
    {
        IReadOnlyList<McpServerConfig> servers =
        [
            new("remote", null, [], new Dictionary<string, string>(),
                Url: "https://x/mcp",
                OAuth: new McpOAuthConfig(ClientId: "cid", Scopes: ["s1"])),
        ];

        var json = McpServerConfig.Serialize(servers);
        Assert.Contains("\"oauth\"", json);
        Assert.Contains("\"client_id\"", json);
        Assert.DoesNotContain("\"client_secret\"", json);   // omitted when absent

        var s = Assert.Single(McpServerConfig.Parse(json));
        Assert.Equal("cid", s.OAuth!.ClientId);
        Assert.Null(s.OAuth.ClientSecret);
        Assert.Equal(["s1"], s.OAuth.Scopes);
    }

    [Fact]
    public void Serialize_HttpServer_RoundTrips_AndOmitsStdioFields()
    {
        IReadOnlyList<McpServerConfig> servers =
        [
            new("remote", null, [], new Dictionary<string, string>(),
                Url: "https://x/mcp",
                Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer ${TOK}" }),
        ];

        var json = McpServerConfig.Serialize(servers);
        Assert.Contains("\"url\"", json);
        Assert.Contains("\"headers\"", json);
        Assert.DoesNotContain("\"command\"", json);

        var s = Assert.Single(McpServerConfig.Parse(json));
        Assert.True(s.IsHttp);
        Assert.Equal("https://x/mcp", s.Url);
        Assert.Equal("Bearer ${TOK}", s.Headers!["Authorization"]);
    }
}
