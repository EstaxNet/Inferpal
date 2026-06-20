using System.Security.Cryptography;
using System.Text;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

public class McpOAuthMetadataTests
{
    // ── PKCE ────────────────────────────────────────────────────────────────

    [Fact]
    public void Pkce_Challenge_IsBase64UrlSha256OfVerifier()
    {
        var (verifier, challenge) = Pkce.Create();

        var expected = Pkce.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Assert.Equal(expected, challenge);
        // base64url alphabet only, no padding.
        Assert.DoesNotContain('+', challenge);
        Assert.DoesNotContain('/', challenge);
        Assert.DoesNotContain('=', challenge);
        Assert.InRange(verifier.Length, 43, 128);
    }

    [Fact]
    public void Pkce_Create_IsRandomEachTime()
    {
        Assert.NotEqual(Pkce.Create().Verifier, Pkce.Create().Verifier);
        Assert.NotEqual(Pkce.NewState(), Pkce.NewState());
    }

    // ── WWW-Authenticate ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("""Bearer resource_metadata="https://mcp.example.com/.well-known/oauth-protected-resource" """,
                "https://mcp.example.com/.well-known/oauth-protected-resource")]
    [InlineData("""Bearer realm="mcp", error="invalid_token", resource_metadata="https://x/prm" """, "https://x/prm")]
    public void ParseResourceMetadataUrl_ExtractsParam(string header, string expected)
    {
        Assert.Equal(expected, McpOAuthMetadata.ParseResourceMetadataUrl(header));
    }

    [Theory]
    [InlineData("Bearer realm=\"mcp\"")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseResourceMetadataUrl_NoParam_ReturnsNull(string? header)
    {
        Assert.Null(McpOAuthMetadata.ParseResourceMetadataUrl(header));
    }

    // ── Protected Resource Metadata (RFC 9728) ────────────────────────────────

    [Fact]
    public void ParseProtectedResourceMetadata_ReadsResourceAndServers()
    {
        var prm = McpOAuthMetadata.ParseProtectedResourceMetadata("""
        {
          "resource": "https://mcp.example.com",
          "authorization_servers": ["https://auth.example.com", "https://auth2.example.com"]
        }
        """);

        Assert.Equal("https://mcp.example.com", prm.Resource);
        Assert.Equal(["https://auth.example.com", "https://auth2.example.com"], prm.AuthorizationServers);
    }

    // ── Authorization Server Metadata (RFC 8414) ──────────────────────────────

    [Fact]
    public void ParseAuthServerMetadata_ReadsEndpointsAndCapabilities()
    {
        var asm = McpOAuthMetadata.ParseAuthServerMetadata("""
        {
          "issuer": "https://auth.example.com",
          "authorization_endpoint": "https://auth.example.com/authorize",
          "token_endpoint": "https://auth.example.com/token",
          "registration_endpoint": "https://auth.example.com/register",
          "scopes_supported": ["mcp.read", "mcp.write"],
          "code_challenge_methods_supported": ["S256"]
        }
        """);

        Assert.Equal("https://auth.example.com", asm.Issuer);
        Assert.Equal("https://auth.example.com/authorize", asm.AuthorizationEndpoint);
        Assert.Equal("https://auth.example.com/token", asm.TokenEndpoint);
        Assert.Equal("https://auth.example.com/register", asm.RegistrationEndpoint);
        Assert.Equal(["mcp.read", "mcp.write"], asm.ScopesSupported);
        Assert.Equal(["S256"], asm.CodeChallengeMethodsSupported);
    }

    [Fact]
    public void ParseAuthServerMetadata_MissingEndpoints_Throws()
    {
        Assert.Throws<FormatException>(() =>
            McpOAuthMetadata.ParseAuthServerMetadata("""{ "issuer": "https://a" }"""));
    }

    // ── Well-known URLs & default endpoints ───────────────────────────────────

    [Fact]
    public void DefaultMetadataUrls_AreOriginRelative_DiscardingPath()
    {
        var server = new Uri("https://api.example.com/v1/mcp");
        Assert.Equal("https://api.example.com/.well-known/oauth-protected-resource",
                     McpOAuthMetadata.DefaultProtectedResourceMetadataUrl(server));
        Assert.Equal("https://auth.example.com/.well-known/oauth-authorization-server",
                     McpOAuthMetadata.DefaultAuthServerMetadataUrl(new Uri("https://auth.example.com/tenant")));
    }

    [Fact]
    public void DefaultEndpoints_UseConventionalPaths()
    {
        var ep = McpOAuthMetadata.DefaultEndpoints(new Uri("https://auth.example.com/ignored/path"));
        Assert.Equal("https://auth.example.com/authorize", ep.AuthorizationEndpoint);
        Assert.Equal("https://auth.example.com/token", ep.TokenEndpoint);
        Assert.Equal("https://auth.example.com/register", ep.RegistrationEndpoint);
    }

    [Theory]
    [InlineData("https://mcp.example.com/mcp", "https://mcp.example.com/mcp")]
    [InlineData("https://mcp.example.com", "https://mcp.example.com")]
    [InlineData("https://mcp.example.com/", "https://mcp.example.com")]
    [InlineData("HTTPS://MCP.Example.COM/mcp", "https://mcp.example.com/mcp")]
    [InlineData("https://mcp.example.com:8443/srv/", "https://mcp.example.com:8443/srv")]
    public void CanonicalResource_NormalizesPerRfc8707(string input, string expected)
    {
        Assert.Equal(expected, McpOAuthMetadata.CanonicalResource(new Uri(input)));
    }
}
