using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Inferpal.Services.Mcp.OAuth;

/// <summary>
/// Encrypts the MCP sign-in tokens off Windows when no editor lends its secret store (an Agent Client Protocol client):
/// one random 256-bit key kept in the system's keychain — macOS's (<c>security</c>, shipped with macOS), Linux's Secret
/// Service (<c>secret-tool</c>, the desktop keyring) — and the token file sealed with it (AES-GCM). Nothing is ever
/// written in clear: without a keychain, <see cref="ForThisSystem"/> gives none and signing in is refused before the browser.
/// </summary>
/// <remarks>The key reaches the keychain through standard input, never a command line another process could read.</remarks>
internal sealed class SystemKeychain
{
    internal const string Service = "Inferpal";
    internal const string Account = "mcp-oauth-key";
    private const byte Version = 1;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<byte[]?> _readKey;
    private readonly Action<byte[]> _writeKey;
    private readonly object _lock = new();
    private byte[]? _key;

    /// <summary>A store over a key read and written by the two delegates (the seam a test fills by hand).</summary>
    internal SystemKeychain(Func<byte[]?> readKey, Action<byte[]> writeKey) => (_readKey, _writeKey) = (readKey, writeKey);

    /// <summary>
    /// The keychain of this machine — one per process, so every session seals with the same key — or <c>null</c> when
    /// it has no keychain tool (Windows: DPAPI does it). Whether the keychain answers is tried before a sign-in opens
    /// the browser (<see cref="McpTokenStore.TryProtect"/>), never at a session's start.
    /// </summary>
    internal static SystemKeychain? ForThisSystem() => Shared.Value;

    private static readonly Lazy<SystemKeychain?> Shared = new(() =>
        OperatingSystem.IsMacOS() && File.Exists("/usr/bin/security") ? new(MacRead, MacWrite)
        : OperatingSystem.IsLinux() && Shell.ShellLauncher.FindOnPath("secret-tool") is { } tool ? new(() => LinuxRead(tool), key => LinuxWrite(tool, key))
        : null);

    /// <summary>The bytes sealed with the keychain's key, its key created the first time.</summary>
    internal byte[] Protect(byte[] data)
    {
        var key = Key(create: true)!;
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var sealedData = new byte[data.Length];
        using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, data, sealedData, tag);
        return [Version, .. nonce, .. tag, .. sealedData];
    }

    /// <summary>The bytes <see cref="Protect"/> sealed. Throws when the key is gone or the bytes were changed.</summary>
    internal byte[] Unprotect(byte[] blob)
    {
        int n = AesGcm.NonceByteSizes.MaxSize, t = AesGcm.TagByteSizes.MaxSize;
        if (blob.Length < 1 + n + t || blob[0] != Version) throw new CryptographicException("Not a token file this keychain sealed.");
        var data = new byte[blob.Length - 1 - n - t];
        for (var attempt = 0; ; attempt++)
        {
            var key = Key(create: false) ?? throw new CryptographicException("The keychain no longer holds Inferpal's key: sign in again.");
            try
            {
                using (var aes = new AesGcm(key, t)) aes.Decrypt(blob.AsSpan(1, n), blob.AsSpan(1 + n + t), blob.AsSpan(1 + n, t), data);
                return data;
            }
            catch (CryptographicException) when (attempt == 0)
            {
                // Another process may have created the key since this one read it: read it again, once.
                lock (_lock) _key = null;
            }
        }
    }

    private byte[]? Key(bool create)
    {
        lock (_lock)
        {
            if (_key is not null) return _key;
            _key = _readKey();
            if (_key is { Length: 32 } || !create) return _key is { Length: 32 } ? _key : null;
            _writeKey(RandomNumberGenerator.GetBytes(32));
            // Read back: what the keychain holds is the key (another process may have written first, an update refused).
            return _key = _readKey() is { Length: 32 } held ? held
                : throw new CryptographicException("The keychain did not keep Inferpal's key.");
        }
    }

    // ── macOS: the login keychain, through /usr/bin/security ──

    private static byte[]? MacRead()
    {
        var run = Run("/usr/bin/security", ["find-generic-password", "-s", Service, "-a", Account, "-w"], null);
        if (run.Exit == 44) return null;   // no such item yet
        return run.Exit == 0 ? Decode(run.Stdout) : throw new CryptographicException($"The macOS keychain did not answer: {run.Stderr.Trim()}");
    }

    private static void MacWrite(byte[] key)
    {
        // Interactive mode reads the command from standard input: the key is on no command line.
        // Its exit code says little: Key() reads the key back.
        Run("/usr/bin/security", ["-i"], $"add-generic-password -U -s {Service} -a {Account} -w {Convert.ToBase64String(key)}\nquit\n");
    }

    // ── Linux: the Secret Service (GNOME Keyring, KWallet), through secret-tool ──

    private static byte[]? LinuxRead(string tool)
    {
        var run = Run(tool, ["lookup", "service", Service, "account", Account], null);
        if (run.Exit == 0) return Decode(run.Stdout);
        // Not found: exit 1 and nothing said. Anything else (no Secret Service, a locked keyring) is not "no key".
        return run.Exit == 1 && run.Stdout.Trim().Length == 0 && run.Stderr.Trim().Length == 0 ? null
            : throw new CryptographicException($"The keyring did not answer: {run.Stderr.Trim()}");
    }

    private static void LinuxWrite(string tool, byte[] key)
    {
        var run = Run(tool, ["store", "--label=Inferpal MCP sign-in", "service", Service, "account", Account], Convert.ToBase64String(key));
        if (run.Exit != 0) throw new CryptographicException($"The keyring refused Inferpal's key: {run.Stderr.Trim()}");
    }

    private static byte[] Decode(string text)
    {
        try { return Convert.FromBase64String(text.Trim()); }
        catch (FormatException) { throw new CryptographicException("The keychain holds something that is not Inferpal's key."); }
    }

    private sealed record Ran(int Exit, string Stdout, string Stderr);

    private static Ran Run(string file, IEnumerable<string> args, string? input)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = ChildProcess.Start(psi);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input is not null) process.StandardInput.Write(input);
        process.StandardInput.Close();
        if (!process.WaitForExit(CallTimeout))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"{Path.GetFileName(file)} did not answer within {CallTimeout.TotalSeconds:N0} s.");
        }
        process.WaitForExit();
        return new(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
