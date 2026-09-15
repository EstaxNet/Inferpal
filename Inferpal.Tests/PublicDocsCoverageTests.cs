using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Inference;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The other half of <see cref="DocCountersTests"/>: that one locks the <b>counters</b>
/// ("N built-in tools", "tests-N"), this one locks the <b>lists</b>.
/// </summary>
/// <remarks>
/// <para>
/// The reason is written inside <c>DocCountersTests</c> itself: every time, the copy left outside
/// the guard was the one that had drifted. A correct counter does not prevent a wrong list - you
/// can document 57 commands, one of which does not exist, and forget another, and the count still
/// adds up.
/// </para>
/// <para>
/// The failure lands in the worst possible order: <b>silent in the repository, visible to the
/// user</b>. Nothing goes red here, and someone types a command the documentation promised them.
/// Measured at zero in all six directions, so it is free to lock.
/// </para>
/// </remarks>
public class PublicDocsCoverageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>One page of the public site, WITH ITS WITNESS.</summary>
    private static string Doc(string name)
    {
        var path = Path.Combine(RepoRoot(), "docs", name);
        Assert.True(File.Exists(path), $"{path} does not exist - the rule checks nothing any more.");

        var text = File.ReadAllText(path);
        Assert.True(text.Length > 500, $"{name} is only {text.Length} character(s) long: the rule is reading an empty page.");
        return text;
    }

    [Fact]
    public void EverySlashCommand_IsDocumented_AndTheDocInventsNone()
    {
        var doc = Doc("slash-commands.md");

        // The underscore is part of the names (/web_search): without it the pattern cuts in the
        // middle and yields "/web", a command that does not exist. False positive paid while
        // writing this test.
        var mentioned = Regex.Matches(doc, @"`(/[a-z0-9_-]+)").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var catalog   = SlashCommandRouter.Catalog.Select(c => c.Cmd).ToHashSet(StringComparer.Ordinal);

        Assert.True(catalog.Count > 20, $"Only {catalog.Count} command(s) in the catalog: the rule compares nothing.");
        Assert.True(mentioned.Count > 20, $"Only {mentioned.Count} command(s) quoted in the page: the pattern reads nothing.");

        var undocumented = catalog.Except(mentioned).Order().ToList();
        Assert.True(undocumented.Count == 0,
            "Shipped command the public site mentions nowhere - it exists and nobody can learn "
            + "about it: " + string.Join(", ", undocumented));

        // The other direction: the docs may quote a legacy ALIAS, absent from the catalog but
        // routed all the same. What they must not do is promise a command the router ignores.
        var router  = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "SlashCommandRouter.cs"));
        var routed  = Regex.Matches(router, @"case ""(/[a-z0-9_-]+)""").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.True(routed.Count > 20, $"Only {routed.Count} routing case(s) read: the second direction judges nothing.");

        var invented = mentioned.Except(catalog).Except(routed).Order().ToList();
        Assert.True(invented.Count == 0,
            "The public site documents a command the router does not know: the user types it and "
            + "nothing happens - " + string.Join(", ", invented));
    }

    [Fact]
    public void EveryConfigKey_IsInTheReference_AndTheReferenceInventsNone()
    {
        var doc = Doc("configuration.md");

        // The real key is the one serialization writes, not the C# property name: read the
        // attribute, never a guessed naming convention.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal.Core", "Config", "InferpalConfig.cs"));
        var real   = Regex.Matches(source, @"\[JsonPropertyName\(""([^""]+)""\)\]")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        // First column of the "Key | Type | Default" tables: what the page presents as an editable
        // key, and nothing else (a key quoted inside a sentence is not an entry).
        var listed = Regex.Matches(doc, @"^\|\s*`([A-Za-z][A-Za-z0-9_.]*)`\s*\|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.True(real.Count > 30, $"Only {real.Count} config key(s) read: the rule compares nothing.");
        Assert.True(listed.Count > 30, $"Only {listed.Count} table row(s) read: the table format changed.");

        var undocumented = real.Except(listed).Order().ToList();
        Assert.True(undocumented.Count == 0,
            "Persisted setting missing from the key reference - it exists in the config file and "
            + "nothing describes it: " + string.Join(", ", undocumented));

        var invented = listed.Except(real).Order().ToList();
        Assert.True(invented.Count == 0,
            "The key reference announces one the configuration never reads: the user writes it into "
            + "their file and it does nothing, with no message at all - "
            + string.Join(", ", invented));
    }

    /// <summary>
    /// A documented <c>provider</c> value is the code the factory reads and the panels write.
    /// </summary>
    /// <remarks>
    /// Both pages gave <c>openai</c>; the code is <c>openai-compatible</c>, and the factory falls back to Ollama on any
    /// unknown code. Copying the documentation therefore made Inferpal talk to another backend than the one written,
    /// and only <c>/diagnostics</c> said so.
    /// </remarks>
    [Fact]
    public void EveryDocumentedProviderValue_IsTheCodeTheFactoryReads()
    {
        var codes = new[] { InferenceProviderFactory.Ollama, InferenceProviderFactory.LmStudio, InferenceProviderFactory.OpenAiCompatible };

        // providers.md: the second column of the providers table (rows whose first cell is bold).
        var documented = Regex.Matches(Doc("providers.md"), @"^\|\s*\*\*[^|]+\*\*\s*\|\s*`([^`]+)`\s*\|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();

        // configuration.md: the description of the `provider` key (| key | type | default | description |).
        var line = Regex.Match(Doc("configuration.md"), @"^\|\s*`provider`\s*\|.*$", RegexOptions.Multiline);
        Assert.True(line.Success, "configuration.md no longer has a row for `provider`.");
        documented.AddRange(Regex.Matches(line.Value.Split('|')[4], "`([^`]+)`").Select(m => m.Groups[1].Value));

        Assert.True(documented.Count >= 6, $"Only {documented.Count} provider value(s) read: the tables have changed shape.");
        var unknown = documented.Where(v => !codes.Contains(v)).Distinct().ToList();
        Assert.True(unknown.Count == 0,
            "The documentation gives a `provider` value that is not the code the factory reads: "
            + string.Join(", ", unknown));
    }

    [Fact]
    public void EveryBuiltInTool_IsDocumented()
    {
        // Same graph the host builds, with MCP and custom tools left empty, so Definitions is
        // exactly the built-in set. Identical to DocCountersTests, so both count the same thing.
        var config   = new InferpalConfig();
        var client   = new FakeInferenceProvider();
        var editor   = new NullEditorSurface();
        var approval = new NoopApproval();
        var index    = new ProjectIndexService(client, config, new LspSemanticProvider());
        var registry = new ToolRegistry(editor, approval, config, index, client,
                                        new ProjectMapService(editor), new McpToolService(config, approval),
                                        new DocsIndexService(client, config), new OpenDocumentOverlay(),
                                        new NullDebugSession());

        var tools = registry.Definitions.Select(d => d.Function.Name).ToList();
        Assert.True(tools.Count > 20, $"Only {tools.Count} tool(s) in the registry: the rule compares nothing.");

        var doc = Doc("tools.md");
        var undocumented = tools.Where(t => !doc.Contains($"`{t}`", StringComparison.Ordinal)).Order().ToList();

        Assert.True(undocumented.Count == 0,
            "Built-in tool that docs/tools.md never mentions. The DocCountersTests counter stays "
            + "correct meanwhile - which is exactly why this rule exists: "
            + string.Join(", ", undocumented));
    }
}
