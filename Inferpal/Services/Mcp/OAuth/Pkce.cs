using System.Security.Cryptography;
using System.Text;

namespace Inferpal.Services.Mcp.OAuth;

/// <summary>
/// Proof Key for Code Exchange (PKCE, RFC 7636) helper — required by the MCP authorization flow.
/// Generates a high-entropy <c>code_verifier</c> and its S256 <c>code_challenge</c>, plus the
/// <c>state</c> nonce used to bind the authorization response to the request.
/// </summary>
internal static class Pkce
{
    /// <summary>The only challenge method the MCP spec mandates support for.</summary>
    public const string Method = "S256";

    /// <summary>Creates a fresh (verifier, challenge) pair. The verifier is 43 base64url chars (32 bytes
    /// of entropy); the challenge is <c>BASE64URL(SHA256(verifier))</c>.</summary>
    public static (string Verifier, string Challenge) Create()
    {
        var verifier  = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    /// <summary>A random opaque <c>state</c> value (43 base64url chars).</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Base64url-encodes <paramref name="data"/> without padding (RFC 4648 §5).</summary>
    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
