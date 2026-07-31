using System.Text;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// Encrypts the MCP OAuth token file through the <b>editor's</b> secret store, over the reverse RPC
/// (`secrets/protect` / `secrets/unprotect`). VS Code answers with its `SecretStorage`, which is
/// backed by the OS keychain — libsecret on Linux, the Keychain on macOS, DPAPI on Windows.
/// </summary>
/// <remarks>
/// <para>
/// Windows hosts keep using DPAPI directly (<see cref="Inferpal.Services.Mcp.OAuth.McpTokenStore"/>'s
/// default): no round-trip, and the file stays readable by the Visual Studio extension, which shares
/// it. Everywhere else there was simply no way to encrypt, so the token store failed loud by design
/// and MCP OAuth was unusable — that is what this closes.
/// </para>
/// <para>
/// The blob never leaves the machine: the adapter stores an opaque base64 payload under a fixed key
/// and hands it back. If the editor cannot honour the request the call throws, which the token store
/// surfaces as "authorization unavailable" rather than silently writing cleartext.
/// </para>
/// </remarks>
internal sealed class RpcSecretStore
{
    /// <summary>Key the adapter files the wrapped blob under, in its own secret storage.</summary>
    private const string SecretKey = "inferpal.mcp.tokens";

    private readonly JsonRpc _rpc;

    public RpcSecretStore(JsonRpc rpc) => _rpc = rpc;

    /// <summary>Hands the plaintext to the editor and returns what it stored back (opaque).</summary>
    public byte[] Protect(byte[] plaintext)
    {
        var wrapped = _rpc.InvokeWithParameterObjectAsync<string>(
            "secrets/protect",
            new { key = SecretKey, value = Convert.ToBase64String(plaintext) })
            .GetAwaiter().GetResult();

        return Encoding.UTF8.GetBytes(wrapped);
    }

    /// <summary>Asks the editor to unwrap a payload previously produced by <see cref="Protect"/>.</summary>
    public byte[] Unprotect(byte[] wrapped)
    {
        var plain = _rpc.InvokeWithParameterObjectAsync<string>(
            "secrets/unprotect",
            new { key = SecretKey, value = Encoding.UTF8.GetString(wrapped) })
            .GetAwaiter().GetResult();

        return Convert.FromBase64String(plain);
    }
}
