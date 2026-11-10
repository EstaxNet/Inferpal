using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Every index a test roots is disposed. A rooted <c>ProjectIndexService</c> arms its FileSystemWatchers, and on Linux
/// each one is an inotify instance, 128 per user, never released while the watcher lives — its reading thread keeps it
/// alive. A handful of test classes that left theirs open took the whole budget in the first seconds of the POSIX run,
/// and every later test that watches the disk timed out, far from the cause.
/// </summary>
public sealed class RootedIndexDisposedTests
{
    private static readonly Regex Created = new(
        @"(?<using>using\s*\(\s*|using\s+)?(?:var\s+)?(?<name>\b[A-Za-z_]\w*)\s*=\s*new\s+(?:[\w.]+\.)?ProjectIndexService\(",
        RegexOptions.Compiled);

    [Fact]
    public void EveryIndexATestRoots_IsDisposed()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var tests = Path.Combine(dir!, "Inferpal.Tests");

        var rooted = 0;
        var leaks  = new List<string>();
        foreach (var file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            var code = ConventionCoverageTests.CodeOnly(file);
            foreach (Match m in Created.Matches(code))
            {
                var name = Regex.Escape(m.Groups["name"].Value);
                if (!Regex.IsMatch(code, $@"\b{name}\.SetRoot\(")) continue;
                rooted++;
                // Disposed where it is declared, by hand, or by the class: kept in one of its collections, which its
                // Dispose empties (_owned, _services).
                var disposed = m.Groups["using"].Success
                               || Regex.IsMatch(code, $@"\b_\w+\.Add\(\s*{name}\s*\)|\b_\w+\.AddRange\([^;]*\b{name}\b|Owned\(\s*{name}\s*\)|\b{name}\??\.Dispose\(");
                if (!disposed) leaks.Add($"{Path.GetFileName(file)}: {m.Groups["name"].Value}");
            }
        }

        Assert.True(rooted >= 20, $"only {rooted} rooted index(es) found: the scan reads nothing");   // witness
        Assert.True(leaks.Count == 0, "Rooted and never disposed (its watchers hold inotify instances for good): "
                                      + string.Join(", ", leaks));
    }
}
