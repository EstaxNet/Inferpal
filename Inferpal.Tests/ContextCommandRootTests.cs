using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Visual Studio's <c>/context</c> refused to show <c>.inferpal/context.md</c> unless a <c>.sln</c> sat at the top of
/// the root — but the root is widened to the folder that holds the solution's projects (<c>SolutionExtent</c>), which
/// has none: "No .sln file found — cannot find .inferpal/context.md", while the system prompt was reading that root's
/// <c>context.md</c> at every question. <c>/memory</c> and the host's <c>/context</c> only refuse an empty root. The
/// command now reads the file from the root the prompt reads it from. The view model is read as source here.
/// </summary>
public class ContextCommandRootTests
{
    private static string Vm(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, "Inferpal", "ToolWindow", file));
    }

    [Fact]
    public void Context_ReadsTheFileFromTheRootTheSystemPromptReads()
    {
        var history = Vm("InferpalToolWindowData.PromptHistory.cs");
        var start   = history.IndexOf("private async Task HandleContextCommandAsync(", StringComparison.Ordinal);
        Assert.True(start > 0, "HandleContextCommandAsync moved: the rule measures nothing");   // WITNESS
        var body = history[start..history.IndexOf("ct));", start, StringComparison.Ordinal)];

        Assert.DoesNotContain("DirectoryHasSolution", body, StringComparison.Ordinal);
        Assert.Contains("FindProjectRoot(), \"context.md\"", body, StringComparison.Ordinal);

        // The same root the prompt injects the file from.
        var rag = Vm("InferpalToolWindowData.Rag.cs");
        var prompt = rag[rag.IndexOf("private string BuildSystemPrompt()", StringComparison.Ordinal)..];
        Assert.Contains("var dir = FindProjectRoot();", prompt[..prompt.IndexOf('}')], StringComparison.Ordinal);
    }
}
