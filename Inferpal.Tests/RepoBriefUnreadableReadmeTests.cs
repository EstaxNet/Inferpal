using System.IO;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A README that exists and does not read is said in the brief of <c>/onboard context</c>, never left out.
/// </summary>
/// <remarks>⚠ The brief becomes the system prompt of every later session: without the line, the repository reads as one
/// without a README.</remarks>
public sealed class RepoBriefUnreadableReadmeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-brief-readme-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Task<(string, int)> Git(string args, CancellationToken _) => Task.FromResult(("feat: x", 0));

    [Fact]
    public async Task AReadmeThatDoesNotRead_IsSaid_NotLeftOut()
    {
        var readme = Path.Combine(_root, "README.md");
        File.WriteAllText(readme, "# Pelican\nThe discount engine.");

        // Reference arm: readable, quoted.
        var read = await OnboardCommandHandler.BuildRepoBriefAsync(_root, Git, CancellationToken.None);
        Assert.Contains("## README.md (excerpt)\n# Pelican", read, StringComparison.Ordinal);

        if (!OperatingSystem.IsWindows()) return;   // a lock that excludes readers is Windows's
        string brief;
        using (new FileStream(readme, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            brief = await OnboardCommandHandler.BuildRepoBriefAsync(_root, Git, CancellationToken.None);

        Assert.Contains("## README.md\n- ⚠ could not be read: read error\n", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Pelican", brief, StringComparison.Ordinal);
    }
}
