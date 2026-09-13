using System.IO;
using System.Linq;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>An unreadable prompt history started empty, and the first prompt sent overwrote the fifty others.</summary>
public class PromptHistoryFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"prompt-history-{Guid.NewGuid():N}");

    public PromptHistoryFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method not found: {signature}");
        var open  = code.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        return string.Empty;
    }

    [Fact]
    public void SavingAfterAnUnreadableHistory_KeepsTheOriginalBytes()
    {
        var path = Path.Combine(_dir, "prompt_history.json");
        var file = PromptHistoryFile.Create();
        file.PathOverride = path;

        Assert.True(file.Save(["explain the vulkan crash", "MARKER-PROMPT"]));
        Assert.Equal(new[] { "explain the vulkan crash", "MARKER-PROMPT" }, file.Load([]));

        var whole = File.ReadAllText(path);
        var torn  = whole[..(whole.Length / 2)];
        File.WriteAllText(path, torn);
        Assert.Empty(file.Load([]));

        Assert.True(file.Save(["a new prompt"]));

        var rescued = Directory.GetFiles(_dir)
            .Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToList();
        Assert.Contains(rescued, text => text == torn);
    }

    [Fact]
    public void TheVsWindow_ReadsAndWritesItsHistoryThroughTheStore()
    {
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.PromptHistory.cs"));
        var save = Body(code, "private void SavePromptHistory(");

        Assert.Contains("_promptHistory.Entries", save, StringComparison.Ordinal);
        Assert.Contains("_promptHistoryStore.Save(", save, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText", save, StringComparison.Ordinal);
        Assert.Contains("_promptHistoryStore.Load(", Body(code, "private void LoadPromptHistory("), StringComparison.Ordinal);
    }
}
