using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio's <c>@problems</c> attaches a build's answer, or says why no build ran — never a "⚠ @problems" chip
/// whose text is that reason (no project, a build stopped at its time limit).
/// </summary>
public sealed class ProblemsMentionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-problems-mention-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task WithNothingToBuild_ItIsANotice_NotAChip()
    {
        File.WriteAllText(Path.Combine(_root, "app.py"), "print('hi')\n");   // a workspace with no .NET project
        using var args = JsonDocument.Parse("{}");
        var answer = await new GetDiagnosticsTool(null, () => _root).ExecuteAsync(args.RootElement, CancellationToken.None);

        var mention = MentionController.ProblemsMention(answer, "⚠ @problems");

        Assert.Null(mention.Label);
        Assert.Equal(Strings.MentionProblemsNotBuilt(Services.Agent.ChatTurnPolicy.OneLinePreview(answer, 240)), mention.Notice);
    }

    /// <summary>Reference arms: a build that ran is attached — its errors, or its clean result.</summary>
    [Fact]
    public void ABuildThatRan_IsAttached()
    {
        var withErrors = "Program.cs(3,5): error CS1002: ; expected";
        Assert.Equal("⚠ @problems", MentionController.ProblemsMention(withErrors, "⚠ @problems").Label);

        var clean = Strings.DiagBuildOk("App.csproj");
        var mention = MentionController.ProblemsMention(clean, "⚠ @problems");
        Assert.Null(mention.Notice);
        Assert.Equal(clean, mention.Content);
    }

    [Fact]
    public void TheVisualStudioWindow_DecidesThroughThePresenter()
    {
        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(
            ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Mentions.cs"));

        Assert.Contains("MentionController.ProblemsMention(diags, \"⚠ @problems\")", vm);
        Assert.DoesNotContain("AddAttachment(\"⚠ @problems\", diags)", vm);
    }
}
