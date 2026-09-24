using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Host;
using StreamJsonRpc;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The files the system prompt carries share ONE budget, set by the context window. Each section had its own
/// ceiling (32 000 characters) and nothing bounded them together: at the default window (8 192 tokens) one section
/// at that ceiling fills the whole window, there are up to seven, and in agent mode the tool definitions already
/// take about 4 900 tokens. The request then overflowed on every question — refused by LM Studio, cut at the head
/// by Ollama, system prompt first — and compaction could not help, the system prompt is never compacted.
/// </summary>
[Collection("Diagnostics")]
public sealed class PromptFileBudgetTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"prompt-budget-{Guid.NewGuid():N}");

    public PromptFileBudgetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Pin(string name, int chars)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, new string('p', chars));
        return path;
    }

    private static IReadOnlyList<PromptSection> Sections(InferpalConfig config, string? root = null,
                                                         IReadOnlySet<string>? disabled = null) =>
        new SystemPromptBuilder(config).BuildSections("base", projectRoot: root, disabledSectionIds: disabled);

    private static PromptSection Pinned(IReadOnlyList<PromptSection> sections, string path) =>
        sections.Single(s => s.Kind == PromptSectionKind.Pinned && s.Key == path);

    [Fact]
    public void OnePinnedFileAtTheOldCeiling_NoLongerFillsTheDefaultWindow()
    {
        var big    = Pin("build.log", SystemPromptBuilder.MaxFileSectionChars);
        var config = new InferpalConfig { PinnedContextFiles = big };   // default window: 8 192 tokens

        var section = Pinned(Sections(config), big).Content;

        Assert.True(section.Length < SystemPromptBuilder.FileSectionsBudget(config.ContextWindowSize) + 400,
                    $"the pinned file still carries {section.Length} characters into an 8 192-token window");
        Assert.Contains("truncated to", section);   // and the cut is said to the model
    }

    [Fact]
    public void TheProjectRules_AreNotStarvedByALargePinnedFile()
    {
        // Reference arm for the sharing rule: a fair share keeps the small section whole.
        Directory.CreateDirectory(Path.Combine(_dir, "repo", ".inferpal", "rules"));
        var rule = "Always use tabs. " + new string('r', 400);
        File.WriteAllText(Path.Combine(_dir, "repo", ".inferpal", "rules", "style.md"), rule);
        var config = new InferpalConfig { PinnedContextFiles = Pin("huge.txt", 100_000) };

        var rules = Sections(config, Path.Combine(_dir, "repo")).Single(s => s.Kind == PromptSectionKind.Rules).Content;

        Assert.Contains(rule, rules);
        Assert.DoesNotContain("truncated to", rules);
    }

    [Fact]
    public void ALargerWindow_CarriesMore()
    {
        // Reference arm: at 32 768 tokens the same file fits, as it did before the shared budget existed.
        var file   = Pin("notes.txt", 30_000);
        var config = new InferpalConfig { PinnedContextFiles = file, ContextWindowSize = 32_768 };

        Assert.DoesNotContain("truncated to", Pinned(Sections(config), file).Content);
    }

    [Fact]
    public void ASectionSwitchedOffInXRay_GivesItsShareBack()
    {
        var a      = Pin("a.txt", 6_000);
        var b      = Pin("b.txt", 6_000);
        var config = new InferpalConfig { PinnedContextFiles = a + "\n" + b };

        var both = Sections(config);
        Assert.Contains("truncated to", Pinned(both, a).Content);   // two 6 000-character files do not fit 8 192
        Assert.Contains("truncated to", Pinned(both, b).Content);

        var withoutB = Sections(config, disabled: new HashSet<string> { $"{PromptSectionKind.Pinned}|{b}" });
        Assert.DoesNotContain("truncated to", Pinned(withoutB, a).Content);
    }

    [Fact]
    public void TheShares_AreFair_AndBoundedByEachSectionsCeiling()
    {
        Assert.Equal([100, 4046, 4046], SystemPromptBuilder.Allot([100, 10_000, 20_000], 8_192));
        Assert.Equal([100, 200], SystemPromptBuilder.Allot([100, 200], 8_192));
        Assert.Equal([SystemPromptBuilder.MaxFileSectionChars],
                     SystemPromptBuilder.Allot([SystemPromptBuilder.MaxFileSectionChars * 3], 1_000_000));
    }

    [Fact]
    public void ACutSection_IsNotedOnce_NotOncePerQuestion()
    {
        // The prompt is rebuilt on every question: one entry per rebuild flushed the 200-entry ring.
        Diagnostics.Clear();
        var config = new InferpalConfig { PinnedContextFiles = Pin("big.txt", 20_000) };

        for (var question = 0; question < 5; question++) Sections(config);

        Assert.Single(Diagnostics.Snapshot(), e => e.Context == "SystemPrompt" && e.Detail.Contains("big.txt"));
    }
}

public partial class HostServerTests
{
    /// <summary>
    /// Under VS Code, <c>/xray</c> built the prompt from the workspace root alone — no persona, no template, no rule
    /// of the active file, no switched-off section — so it described another prompt than the one the next question
    /// carries, and than the X-Ray panel of the same host shows.
    /// </summary>
    [Fact]
    public async Task SlashXray_DescribesThePromptTheNextQuestionCarries()
    {
        var root = NewRootWithCSharpRule();
        try
        {
            using var h = CreateHarness();
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            await h.Client.NotifyWithParameterObjectAsync("editor/didChangeActiveDocument",
                new { path = Path.Combine(root, "src", "Program.cs") });
            await WaitForXraySectionAsync(h, id => id == "Rules");   // witness

            var xray = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
                "command/slash", new { text = "/xray" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.Contains(Inferpal.Localization.Strings.XrayLabelPersona, xray.Markdown);
            Assert.Contains(Inferpal.Localization.Strings.XrayLabelRules("1"), xray.Markdown);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
