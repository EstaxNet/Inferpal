using System.IO;
using Inferpal.Config;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The system prompt states the workspace root the file tools confine to.
//
//  Only a .NET solution put an absolute path in front of the model (the solution block's Location). In a
//  JavaScript or Python project qwen3.6-27b started 3 of 6 real tasks under an invented root —
//  /home/user/repos/… — refused as outside the workspace, then found again with `pwd`. Replayed ten times,
//  the first call of a captured run went to /home/user 7 times; with the root stated, 0 times, and no reply
//  lost its call.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(ShellSerialCollection.Name)]
public class WorkspaceRootFactTests
{
    [Fact]
    public void TheFacts_StateTheWorkspaceRoot()
    {
        var root  = Path.Combine(Path.GetTempPath(), "shop");
        var facts = new SystemPromptBuilder(new InferpalConfig(), "Visual Studio Code", 0, root).EnvironmentFacts();

        Assert.Contains($"The workspace root is {root}.", facts);
        // It reaches the prompt the model receives, in the base layer (no new X-Ray section).
        var sections = new SystemPromptBuilder(new InferpalConfig(), null, 0, root).BuildSections("BASE");
        Assert.Contains($"The workspace root is {root}.", sections[0].Content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoRootPinned_NoRootStated(string? root)
    {
        // Reference arm: without a pinned root the sentence is left out — a guessed root the tools would refuse
        // is worse than none.
        var facts = new SystemPromptBuilder(new InferpalConfig(), "Visual Studio", 0, root).EnvironmentFacts();

        Assert.DoesNotContain("workspace root", facts);
        Assert.Contains("Operating system:", facts);   // witness: the facts are there
    }

    [Theory]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs")]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.Xray.cs")]
    [InlineData("Inferpal.Host", "HostServer.cs")]
    public void EveryPromptBuild_StatesTheRootTheToolsUse(params string[] parts)
    {
        // The tools confine to the index service's RootDir in both front-ends. The view model's FindProjectRoot
        // falls back to the process's own directory: stated to the model, a root every tool refuses.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code  = ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, Path.Combine(parts)));
        var sites = code.Split("new SystemPromptBuilder(").Skip(1).Select(s => s[..s.IndexOf(')')]).ToList();

        Assert.NotEmpty(sites);   // witness
        Assert.All(sites, args =>
        {
            Assert.Contains(".RootDir", args, StringComparison.Ordinal);
            Assert.DoesNotContain("FindProjectRoot", args, StringComparison.Ordinal);
        });
    }
}
