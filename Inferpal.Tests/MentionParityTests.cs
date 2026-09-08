using System.IO;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An <c>@</c>-mention category the host serves is never answered again by the adapter.
/// </summary>
/// <remarks>
/// <para>
/// This is exactly how <c>@debugger</c> drifted. The host could render the break state — that is
/// what <c>docs/mentions.md</c> promises to <b>both</b> editors — but <c>chatViewProvider.ts</c>
/// answered it itself, from <c>vscode.debug.activeDebugSession</c>, and attached the session's
/// <b>name</b> where Visual Studio attached the call stack and the locals. Two answers to the same
/// question, only one of them right, and nothing to say so: no compiler, no test, no runtime error.
/// </para>
/// <para>
/// The rule is about the <b>property</b> — an empty overlap — not about a list of categories. What
/// legitimately stays editor-side (<c>clipboard</c>, <c>problems</c>) does so because the host
/// cannot see those panels, and a category added tomorrow inherits the rule without anyone
/// remembering to add it here.
/// </para>
/// </remarks>
public class MentionParityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void ACategoryTheHostServes_IsNeverAnsweredByTheAdapterItself()
    {
        var hostServed = HostServedCategories();
        var editorSide = AdapterAnsweredCategories();

        // Witnesses: two empty lists would overlap in nothing, which is a green that measures
        // nothing. The host serves five today, the adapter keeps two.
        Assert.True(hostServed.Count >= 4,
            $"The scan found only {hostServed.Count} category/ies in MentionResolveAsync: the rule no longer checks anything.");
        Assert.True(editorSide.Count >= 2,
            $"The scan found only {editorSide.Count} category/ies in resolveMention: the rule no longer checks anything.");

        var both = hostServed.Intersect(editorSide).OrderBy(c => c).ToList();

        Assert.True(both.Count == 0,
            "An @mention category answered TWICE — once by the host, once by the VS Code adapter. "
            + "The two answers drift apart in silence (which is what happened to @debugger: a session "
            + "name on one side, a call stack on the other). Remove the branch from "
            + "chatViewProvider.ts and let it fall through to `default`, which calls mention/resolve:"
            + Environment.NewLine + "  " + string.Join(", ", both));
    }

    /// <summary>The categories <c>mention/resolve</c> handles, read from the syntax tree.</summary>
    private static IReadOnlyCollection<string> HostServedCategories()
    {
        var path = Path.Combine(RepoRoot(), "Inferpal.Host", "HostSlashCommands.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.ValueText == "MentionResolveAsync");
        Assert.True(method is not null, $"MentionResolveAsync is missing from {path}: the rule no longer checks anything.");

        return method!.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(l => l.Token.ValueText)
            .Where(v => Regex.IsMatch(v, "^[a-z]+$"))     // the `case` labels, not the emojis or the chip labels
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The categories the adapter answers without asking the host.</summary>
    private static IReadOnlyCollection<string> AdapterAnsweredCategories()
    {
        var path = Path.Combine(RepoRoot(), "vscode", "src", "chatViewProvider.ts");
        var code = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(path));

        var start = code.IndexOf("resolveMention(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"resolveMention is missing from {path}: the rule no longer checks anything.");

        // Up to `default:` — past that is precisely the branch that delegates to the host.
        var end = code.IndexOf("default:", start, StringComparison.Ordinal);
        Assert.True(end > start, "The `default:` of resolveMention is missing: the rule no longer checks anything.");

        return Regex.Matches(code[start..end], @"case '([a-z]+)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
