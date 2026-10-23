using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A key belongs to its server: a form or a role's Test that sends no key probes an address of its own, and the chat
/// server's saved key goes there only when that address IS the chat server's (<c>HostServer.SavedKeyFor</c>).
/// </summary>
public class SavedKeyTests
{
    [Fact]
    public void TheChatServersKey_GoesToNoOtherAddress()
    {
        var host = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Host");
        var sources = Directory.EnumerateFiles(host, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetFileName(f), ConventionCoverageTests.CodeOnly);
        Assert.True(sources.Count > 20, "the host's sources are not read");                                    // witness

        // No fallback to the saved key whatever the address: through SavedKeyFor, which compares the addresses.
        var leaks = sources.Where(kv => Regex.IsMatch(kv.Value, @"ApiKey\s*\?\?\s*s\.Config\.ApiKey")).Select(kv => kv.Key).ToList();
        Assert.Empty(leaks);
        var all = string.Concat(sources.Values);
        Assert.Contains("ServerScan.SameAddress(url, s.Config.BaseUrl) ? s.Config.ApiKey ?? \"\" : \"\"", all, StringComparison.Ordinal);
        Assert.True(Regex.Matches(all, @"p\.ApiKey \?\? SavedKeyFor\(s, url\)").Count >= 3, "the forms' probes read the key for their address");
    }
}
