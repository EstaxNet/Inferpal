using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The documentation is reachable from the product: <c>/help</c> ends on it (Visual Studio, VS Code, the ACP clients) and
/// the chat's header has a "?" that opens it (Visual Studio's header, VS Code's view title and command palette) — the
/// docs of the version installed, never <c>master</c>, whose pages describe what is developed since.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class DocumentationAccessTests
{
    private static string Root => ConventionCoverageTests.RepoRoot();

    [Fact]
    public void TheAddress_IsTheInstalledVersionsTag_AndItsIndexExists()
    {
        Assert.Equal("v1.7.7", DocsLinks.RefFor("1.7.7"));
        // Not a release number: master.
        foreach (var dev in new[] { "0.0.0-dev", "1.7", "1.7.7.0", "", null })
            Assert.Equal("master", DocsLinks.RefFor(dev));

        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        var version = Regex.Match(props, @"<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.Equal($"{DocsLinks.Repository}/blob/v{version}/docs/README.md", DocsLinks.Index);
        Assert.True(File.Exists(Path.Combine(Root, "docs", "README.md")));
        // A release is published under that tag.
        Assert.Contains("tags: ['v*']", File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml")), StringComparison.Ordinal);
    }

    /// <summary>
    /// What is published beside a release — the two Marketplace listings, the VSIX manifest, the site — links the docs of
    /// the version it ships, like the product: a listing is only changed by publishing it, and <c>master</c> describes
    /// what is not released yet. Red between a version bump and the links following it (the release wizard's bump
    /// rewrites them). The ACP registry entry is the exception (<see cref="TheRegistryEntry_LinksTheDocsOnMaster"/>).
    /// </summary>
    [Theory]
    [InlineData("MARKETPLACE.md", true)]                          // private repo only
    [InlineData("site/index.html", true)]                         // private repo only
    [InlineData("vscode/README.md", false)]
    [InlineData("Inferpal/source.extension.vsixmanifest", false)]
    public void TheListings_LinkTheDocsOfTheVersionTheyShip(string listing, bool privateOnly)
    {
        var path = Path.Combine(Root, listing);
        if (privateOnly && !File.Exists(path)) return;
        var version = Regex.Match(File.ReadAllText(Path.Combine(Root, "Directory.Build.props")), @"<Version>([^<]+)</Version>").Groups[1].Value;
        var refs = Regex.Matches(File.ReadAllText(path), @"github\.com/EstaxNet/Inferpal/blob/([^/]+)/")
                        .Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(refs);   // witness: the listing still links the docs
        Assert.All(refs, r => Assert.Equal($"v{version}", r));
    }

    /// <summary>
    /// The ACP registry follows each release by itself, but rewrites only the archive URLs: a docs link pinned to a tag
    /// would stay on the version the entry was first sent with. Its docs link is on <c>master</c>; its license may stay
    /// pinned (the text does not change).
    /// </summary>
    [Fact]
    public void TheRegistryEntry_LinksTheDocsOnMaster()
    {
        var script = File.ReadAllText(Path.Combine(Root, "acp", "registry-entry.ps1"));
        var website = Regex.Match(script, @"website\s*=\s*'([^']+)'");
        Assert.True(website.Success, "acp/registry-entry.ps1 no longer writes a website link");   // witness
        Assert.Equal("https://github.com/EstaxNet/Inferpal/blob/master/docs/acp.md", website.Groups[1].Value);
    }

    /// <summary>
    /// <c>/help</c> ends on the documentation, its address the link's text: readable as it is wherever a link cannot be
    /// followed — a copy, an export.
    /// </summary>
    [Fact]
    public void Help_EndsOnTheDocumentation_ItsAddressReadableInVisualStudio()
    {
        Strings.ApplyLanguage("fr");
        try
        {
            var help = SlashCommandRouter.BuildHelp();
            Assert.EndsWith(Strings.HelpDocumentation($"[{DocsLinks.Index}]({DocsLinks.Index})"), help, StringComparison.Ordinal);

            var shown = string.Concat(MarkdownParser.Parse(help).SelectMany(b => b.Inlines).Select(r => r.Text));
            Assert.Contains(DocsLinks.Index, shown, StringComparison.Ordinal);
            Assert.Contains("(en anglais)", shown, StringComparison.Ordinal);   // the docs are in English: said
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public void VisualStudiosHeader_HasTheDocumentationButton()
    {
        var xaml = File.ReadAllText(Path.Combine(Root, "Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml"));
        Assert.Matches(@"Content=""&#xE897;""[^>]*Command=""\{Binding OpenDocumentationCommand\}""[^>]*ToolTip=""\{Binding LabelDocumentation\}"" AutomationProperties\.Name=""\{Binding LabelDocumentation\}""", xaml);

        var chrome = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"));
        Assert.Contains("var docs = DocsLinks.Index;", chrome, StringComparison.Ordinal);
        Assert.Contains("LabelDocumentation   = Strings.ChatDocumentation;", chrome, StringComparison.Ordinal);
        // Not opened, the address is said.
        Assert.Contains("await ShowInfoAsync(Strings.HelpDocumentation(docs));", chrome, StringComparison.Ordinal);
    }

    /// <summary>VS Code builds the address from its own version — the same address as the Core's, both ways.</summary>
    [Fact]
    public void VsCode_OpensTheSameAddress_FromBesideTheGear()
    {
        var links = WebviewRebuildTests.TsCode("docsLinks.ts");
        Assert.Contains($"export const repository = '{DocsLinks.Repository}';", links, StringComparison.Ordinal);
        Assert.Contains(@"/^\d+\.\d+\.\d+$/.test(version) ? `v${version}` : 'master'", links, StringComparison.Ordinal);
        Assert.Contains("return `${repository}/blob/${ref}/docs/${page}`;", links, StringComparison.Ordinal);

        var extension = WebviewRebuildTests.TsCode("extension.ts");
        Assert.Contains("registerCommand('inferpal.openDocs'", extension, StringComparison.Ordinal);
        Assert.Contains("docsUrl(context.extension.packageJSON.version", extension, StringComparison.Ordinal);

        // In the view's title bar, right after the gear, with the question-mark icon.
        var manifest = File.ReadAllText(Path.Combine(Root, "vscode", "package.json"));
        Assert.Matches(@"""command"": ""inferpal\.openDocs"",\s*""title"": ""%inferpal\.command\.openDocs%"",\s*""icon"": ""\$\(question\)""", manifest);
        Assert.Matches(@"""command"": ""inferpal\.openDocs"",\s*""when"": ""view == inferpal\.chat"",\s*""group"": ""navigation@6""", manifest);
        Assert.Matches(@"""command"": ""inferpal\.openSettings"",\s*""when"": ""view == inferpal\.chat"",\s*""group"": ""navigation@5""", manifest);
    }
}
