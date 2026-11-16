using System.IO;
using System.Security.Cryptography;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// MCP sign-in off Windows with no editor's secret store (an Agent Client Protocol client): the token file sealed with a
/// key the system's keychain holds — macOS's, Linux's Secret Service — created once, never written in clear.
/// </summary>
public sealed class SystemKeychainTests
{
    private sealed class Keyring
    {
        public byte[]? Key;
        public int Writes;
        public SystemKeychain Store() => new(() => Key, k => { Key = k; Writes++; });
    }

    [Fact]
    public void TheTokens_GoThroughTheKeychainsKey_AndComeBack()
    {
        var keyring = new Keyring();
        var tokens = "{\"access_token\":\"secret-value\"}"u8.ToArray();

        var sealedTokens = keyring.Store().Protect(tokens);

        Assert.Equal(32, keyring.Key!.Length);
        Assert.DoesNotContain("secret-value", System.Text.Encoding.UTF8.GetString(sealedTokens), StringComparison.Ordinal);
        Assert.Equal(tokens, keyring.Store().Unprotect(sealedTokens));   // another session, the same keychain
    }

    [Fact]
    public void TheKey_IsCreatedOnce_ThenReused()
    {
        var keyring = new Keyring();
        var store = keyring.Store();
        store.Protect([1]);
        store.Protect([2]);
        keyring.Store().Protect([3]);
        Assert.Equal(1, keyring.Writes);
    }

    [Fact]
    public void AKeyTheKeychainNoLongerHolds_OrAChangedFile_IsRefused_NeverReadAsTokens()
    {
        var keyring = new Keyring();
        var sealedTokens = keyring.Store().Protect([1, 2, 3]);

        var tampered = (byte[])sealedTokens.Clone();
        tampered[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => keyring.Store().Unprotect(tampered));
        Assert.ThrowsAny<CryptographicException>(() => new Keyring().Store().Unprotect(sealedTokens));
    }

    [Fact]
    public void AKeychainThatDoesNotAnswer_IsNotAMissingKey_NoKeyIsWrittenOverTheRealOne()
    {
        var writes = 0;
        var locked = new SystemKeychain(() => throw new CryptographicException("locked"), _ => writes++);

        Assert.ThrowsAny<CryptographicException>(() => locked.Protect([1]));
        Assert.Equal(0, writes);
        Assert.False(new McpTokenStore(Path.Combine(Path.GetTempPath(), "never-written.dat"), locked.Protect, locked.Unprotect).TryProtect());
    }

    [Fact]
    public void TheFile_IsSealedWithTheKeyTheKeychainHolds_NotTheOneThisProcessMade()
    {
        // Another process wrote first, and this write was refused: the keychain keeps the other's key.
        var held = RandomNumberGenerator.GetBytes(32);
        var calls = 0;
        var store = new SystemKeychain(() => calls++ == 0 ? null : held, _ => { });

        var sealedTokens = store.Protect([7, 8, 9]);

        Assert.Equal([7, 8, 9], new SystemKeychain(() => held, _ => { }).Unprotect(sealedTokens));
    }

    [Fact]
    public void ATokenSealedByAnotherProcess_AfterThisOneReadTheKey_IsReadAgainOnce()
    {
        var keyring = new Keyring();
        var mine = keyring.Store();
        mine.Protect([1]);                                   // this process caches the key
        keyring.Key = RandomNumberGenerator.GetBytes(32);    // another one replaced it, and sealed with it
        var theirs = keyring.Store().Protect([4, 2]);
        Assert.Equal([4, 2], mine.Unprotect(theirs));
    }

    [Fact]
    public void TheTokenStore_OverTheKeychain_CanProtect_AndKeepsASignIn()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("inferpal-keychain-").FullName, "mcp-oauth.dat");
        var keyring = new Keyring();
        var keychain = keyring.Store();
        var store = new McpTokenStore(path, keychain.Protect, keychain.Unprotect);
        Assert.True(store.CanProtect);

        store.Save("docs", new McpOAuthState { AccessToken = "token-1" });

        var again = keyring.Store();
        Assert.Equal("token-1", new McpTokenStore(path, again.Protect, again.Unprotect).Get("docs")?.AccessToken);
        Assert.DoesNotContain("token-1", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
