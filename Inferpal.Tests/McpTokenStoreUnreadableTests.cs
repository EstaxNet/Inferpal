using System.IO;
using System.Text;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A sign-in store that cannot be read is not an empty one: saving a sign-in never erases the others.
/// </summary>
/// <remarks>
/// An unreadable file (another user's encryption, a locked keychain, the other editor's write in progress) was read as
/// "no sign-ins", and the next sign-in rewrote the file with itself alone: every other server's sign-in was gone.
/// </remarks>
public sealed class McpTokenStoreUnreadableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"mcp-store-{Guid.NewGuid():N}");
    private string Path_ => Path.Combine(_dir, "mcp-oauth.dat");

    public McpTokenStoreUnreadableTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static McpOAuthState Signed(string token) => new() { Resource = "https://x/mcp", AccessToken = token };

    private void TwoSignIns()
    {
        var store = new McpTokenStore(Path_, protect: b => b, unprotect: b => b);
        store.Save("github", Signed("gh"));
        store.Save("linear", Signed("ln"));
    }

    [Fact]
    public void AFileThatCannotBeRead_IsKeptAside_BeforeASignInIsSaved()
    {
        TwoSignIns();
        var before = File.ReadAllBytes(Path_);
        var store = new McpTokenStore(Path_, protect: b => b,
                                      unprotect: _ => throw new System.Security.Cryptography.CryptographicException("Key not valid for use in specified state."));

        Assert.Null(store.Get("github"));                      // nothing can be read from it…
        store.Save("notion", Signed("nt"));                    // …and a new sign-in is still saved

        var aside = Assert.Single(Directory.GetFiles(_dir, "mcp-oauth.dat.unreadable-*"));
        Assert.Equal(before, File.ReadAllBytes(aside));        // the other sign-ins are still on disk
        var now = Encoding.UTF8.GetString(File.ReadAllBytes(Path_));
        Assert.Contains("\"notion\"", now, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileUnreadableForAMoment_KeepsEverySignIn()
    {
        TwoSignIns();
        var failures = 1;                                       // the other editor was replacing it: the first read fails
        var store = new McpTokenStore(Path_, protect: b => b,
                                      unprotect: b => failures-- > 0 ? throw new IOException("The process cannot access the file.") : b);

        store.Save("notion", Signed("nt"));

        var fresh = new McpTokenStore(Path_, protect: b => b, unprotect: b => b);
        Assert.Equal(["gh", "ln", "nt"], new[] { "github", "linear", "notion" }.Select(n => fresh.Get(n)?.AccessToken));
        Assert.Empty(Directory.GetFiles(_dir, "*.unreadable-*"));
    }

    [Fact]
    public void AReadableFile_IsNeverCopied()
    {
        // Reference arm: an ordinary save keeps the others and copies nothing.
        TwoSignIns();
        var store = new McpTokenStore(Path_, protect: b => b, unprotect: b => b);

        store.Save("notion", Signed("nt"));

        Assert.Equal("gh", store.Get("github")?.AccessToken);
        Assert.Empty(Directory.GetFiles(_dir, "*.unreadable-*"));
    }
}
