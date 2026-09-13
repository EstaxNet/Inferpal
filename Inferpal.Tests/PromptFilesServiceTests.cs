using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <see cref="PromptFilesService"/>'s cache is a single static slot: any <c>Load</c> of another folder, or
/// an invalidation, from a class running in parallel replaces it — and every slash command the host routes
/// loads the templates. Tests that assert on that cache run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PromptFilesCacheCollection
{
    public const string Name = "PromptFilesCache";
}

[Collection(PromptFilesCacheCollection.Name)]
public class PromptFilesServiceTests : IDisposable
{
    private readonly string _dir;

    public PromptFilesServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"prompts_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        PromptFilesService.InvalidateCache();
    }

    public void Dispose()
    {
        PromptFilesService.InvalidateCache();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void WritePrompt(string fileName, string content) =>
        File.WriteAllText(Path.Combine(_dir, fileName), content);

    // ── Loading ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_MissingDir_ReturnsEmpty()
    {
        Assert.Empty(PromptFilesService.LoadUncached(Path.Combine(_dir, "nope")));
        Assert.Empty(PromptFilesService.LoadUncached(string.Empty));
    }

    [Fact]
    public void Load_FileNameBecomesSlashCommand()
    {
        WritePrompt("review-security.md", "Review this code.");

        var prompts = PromptFilesService.LoadUncached(_dir);

        Assert.Single(prompts);
        Assert.Equal("/review-security", prompts[0].Name);
        Assert.Equal("Review this code.", prompts[0].Text);
        Assert.Null(prompts[0].Hint);
    }

    [Fact]
    public void Load_FrontmatterDescription_BecomesHint_AndIsStrippedFromBody()
    {
        WritePrompt("perf.md", "---\ndescription: Performance review\n---\nFind hot paths.\n");

        var prompts = PromptFilesService.LoadUncached(_dir);

        Assert.Equal("Performance review", prompts[0].Hint);
        Assert.Equal("Find hot paths.", prompts[0].Text);
    }

    [Fact]
    public void Load_EmptyBody_IsSkipped()
    {
        WritePrompt("empty.md", "---\ndescription: nothing\n---\n   \n");
        WritePrompt("blank.md", "");

        Assert.Empty(PromptFilesService.LoadUncached(_dir));
    }

    [Fact]
    public void Load_NonMdFiles_AreIgnored()
    {
        WritePrompt("readme.txt", "not a prompt");
        WritePrompt("real.md", "a prompt");

        Assert.Single(PromptFilesService.LoadUncached(_dir));
    }

    [Fact]
    public void Load_OrderedByFileName()
    {
        WritePrompt("zeta.md", "z");
        WritePrompt("alpha.md", "a");

        var prompts = PromptFilesService.LoadUncached(_dir);

        Assert.Equal(["/alpha", "/zeta"], prompts.Select(p => p.Name));
    }

    [Theory]
    [InlineData("Review Security", "review-security")]
    [InlineData("my_prompt", "my-prompt")]
    [InlineData("UPPER", "upper")]
    public void CommandName_LowercasesAndDashes(string fileName, string expected)
    {
        Assert.Equal(expected, PromptFilesService.CommandName(fileName));
    }

    // ── Cache ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_CachesWithinTtl()
    {
        WritePrompt("one.md", "first");
        var first = PromptFilesService.Load(_dir);

        WritePrompt("two.md", "second");
        var second = PromptFilesService.Load(_dir);   // within TTL → stale by design

        Assert.Single(first);
        Assert.Same(first, second);

        PromptFilesService.InvalidateCache();
        Assert.Equal(2, PromptFilesService.Load(_dir).Count);
    }

    // ── Router integration ──────────────────────────────────────────────────────

    /// <summary>
    /// A template named like a built-in command never runs — the router answers the built-in first — so it
    /// is not offered either: autocomplete listed <c>/clear</c> twice, the second entry with the template's
    /// hint, and picking it cleared the conversation.
    /// </summary>
    /// <remarks>
    /// Lives in this class, not with the router tests: it goes through the cached loader and invalidates the
    /// cache, and a parallel class doing that broke <see cref="Load_CachesWithinTtl"/>'s <c>Assert.Same</c>.
    /// </remarks>
    [Fact]
    public void ATemplateNamedLikeABuiltIn_IsNotLoaded_NorOfferedInAutocomplete()
    {
        var root    = Path.Combine(Path.GetTempPath(), $"shadowed_prompts_{Guid.NewGuid():N}");
        var prompts = Path.Combine(root, ".inferpal", "prompts");
        Directory.CreateDirectory(prompts);
        try
        {
            File.WriteAllText(Path.Combine(prompts, "undo-run.md"), "Summarize the last run.");
            var config = new Inferpal.Config.InferpalConfig
            {
                PromptTemplates = "/clear=Summarize the conversation\n/mine=Hello {args}",
            };

            var templates = SlashTemplates.Load(config, root);

            Assert.Equal("/mine", Assert.Single(templates).Name);   // witness: a free name stays
            Assert.Single(SlashCommandRouter.MatchCommands("/clear", templates), m => m.Cmd == "/clear");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Route_UnknownCommand_FallsBackToPromptFile_WithArgsExpansion()
    {
        WritePrompt("translate.md", "Translate to French: {args}");
        var templates = PromptFilesService.LoadUncached(_dir);

        var action = SlashCommandRouter.Route("/translate hello world", templates);

        var prompt = Assert.IsType<SlashPromptAction>(action);
        Assert.Equal("Translate to French: hello world", prompt.Prompt);
    }

    [Fact]
    public void MatchCommands_UsesDescriptionHintOverTruncatedText()
    {
        WritePrompt("perf.md", "---\ndescription: Performance review\n---\n" + new string('x', 200));
        var templates = PromptFilesService.LoadUncached(_dir);

        var matches = SlashCommandRouter.MatchCommands("/pe", templates);

        var match = Assert.Single(matches, m => m.Cmd == "/perf");
        Assert.Equal("Performance review", match.Hint);
    }
}
