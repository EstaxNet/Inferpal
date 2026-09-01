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

    /// <summary>
    /// Ceiling on one round-trip to the editor. The call is synchronous by contract — the token
    /// store's interface is <c>byte[] Protect(byte[])</c>, shared with the DPAPI implementation —
    /// so without a bound the calling thread waits forever on an editor that never answers: a
    /// closed panel, a busy adapter, an extension host that crashed between request and reply.
    /// Ten seconds is far past a keychain round-trip and far short of "the session is stuck".
    /// </summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly JsonRpc _rpc;

    public RpcSecretStore(JsonRpc rpc) => _rpc = rpc;

    /// <summary>
    /// Blocks on one reverse RPC with a deadline, unwrapping the usual
    /// <see cref="AggregateException"/> so callers see the failure the adapter reported.
    /// </summary>
    private T Invoke<T>(string method, object arguments)
    {
        using var budget = new CancellationTokenSource(CallTimeout);
        try
        {
            return _rpc.InvokeWithParameterObjectAsync<T>(method, arguments, budget.Token)
                       .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The editor did not answer '{method}' within {CallTimeout.TotalSeconds:N0} s; " +
                "the MCP token store cannot encrypt right now.");
        }
    }

    /// <summary>Hands the plaintext to the editor and returns what it stored back (opaque).</summary>
    public byte[] Protect(byte[] plaintext)
    {
        var wrapped = Invoke<string>(
            "secrets/protect",
            new { key = SecretKey, value = Convert.ToBase64String(plaintext) });

        return Encoding.UTF8.GetBytes(wrapped);
    }

    /// <summary>Asks the editor to unwrap a payload previously produced by <see cref="Protect"/>.</summary>
    public byte[] Unprotect(byte[] wrapped)
    {
        var plain = Invoke<string>(
            "secrets/unprotect",
            new { key = SecretKey, value = Encoding.UTF8.GetString(wrapped) });

        return Convert.FromBase64String(plain);
    }
}
