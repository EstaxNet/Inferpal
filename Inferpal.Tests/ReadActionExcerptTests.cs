using System.IO;
using System.Text;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Services.CodeActions;
using StreamJsonRpc;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ In VS Code, <c>/explain</c> and <c>/review</c> fenced the WHOLE active file into the prompt: no
/// budget at all, where the Visual Studio window cuts to one and says so. A file larger than the
/// context window then goes out whole, and past the window the backend drops the HEAD of the request —
/// the system prompt and the "review the following code" instruction first — without a word: the model
/// answers about a fragment it was never asked about. The excerpt now comes from the host, sized from
/// the configured window, and so does Visual Studio's: the default window keeps the budget it had, a
/// larger one no longer loses code that fits.
/// </summary>
public class ReadActionExcerptTests
{
    internal static string Source(int lines)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= lines; i++) sb.Append("line ").Append(i.ToString("D4")).Append(" of the source file ...\n");
        return sb.ToString();                               // 30 characters a line
    }

    [Fact]
    public void AtTheDefaultWindow_TheBudgetIsTheOneVisualStudioAlreadyUsed()
    {
        Assert.Equal(CodeExcerpt.MaxChars, CodeExcerpt.BudgetFor(new InferpalConfig().ContextWindowSize));

        // A smaller or unset window never shrinks it: that is the budget both editors were validated with.
        Assert.Equal(CodeExcerpt.MaxChars, CodeExcerpt.BudgetFor(2_048));
        Assert.Equal(CodeExcerpt.MaxChars, CodeExcerpt.BudgetFor(0));
    }

    [Fact]
    public void ALargerWindow_KeepsTheSameShareOfIt_SoAFileThatFitsIsNotCut()
    {
        // Reference arm for VS Code: with a 32k window, a 20,000-character file went out whole and fit. A
        // fixed budget would cut it — the fix must not trade a silent overflow for a needless cut.
        var code = Source(650);
        Assert.True(code.Length > 19_000, $"the file is only {code.Length} characters");

        Assert.Equal(CodeExcerpt.MaxChars * 4, CodeExcerpt.BudgetFor(new InferpalConfig().ContextWindowSize * 4));
        Assert.False(CodeExcerpt.Of(code, CodeExcerpt.BudgetFor(32_768)).IsTruncated);
    }

    [Fact]
    public void EveryExcerptSite_IsSizedByTheConfiguredWindow()
    {
        var root  = RepoRoot();
        var sites = new[] { "Inferpal", "Inferpal.Host" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsBuildOutput(f))
            .SelectMany(f => ConventionCoverageTests.CodeOnly(f).Split('\n')
                                                    .Select(line => (File: Path.GetFileName(f), Line: line)))
            .Where(x => x.Line.Contains("CodeExcerpt.Of(", StringComparison.Ordinal))
            .ToList();

        // WITNESS: the two Visual Studio sites (chat command, editor context menu) and the host's.
        Assert.True(sites.Count >= 3, $"only {sites.Count} excerpt site(s) found — the scan is not reading what it should");
        Assert.All(sites, s => Assert.True(s.Line.Contains("CodeExcerpt.BudgetFor(", StringComparison.Ordinal),
            $"{s.File}: an excerpt with the fixed budget — {s.Line.Trim()}"));
    }

    [Fact]
    public void VsCodeExplainAndReview_SendTheHostsExcerpt_AndNameItUnderTheQuestion()
    {
        var body = RunExplainReviewBody();

        // WITNESS: this is the method that reads the editor's text.
        Assert.Contains("getText(", body, StringComparison.Ordinal);

        Assert.Contains("codeExcerpt(", body, StringComparison.Ordinal);
        Assert.Contains("nameAttachmentsInQuestion(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("${code}", body, StringComparison.Ordinal);   // the raw text never reaches the prompt
    }

    private static string RunExplainReviewBody()
    {
        var path  = Path.Combine(RepoRoot(), "vscode", "src", "chatViewProvider.ts");
        var text  = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(path));
        var start = text.IndexOf("private async runExplainReview(", StringComparison.Ordinal);
        Assert.True(start >= 0, "runExplainReview is gone from chatViewProvider.ts");
        var end = text.IndexOf("\n  private ", start + 1, StringComparison.Ordinal);
        return end > 0 ? text[start..end] : text[start..];
    }

    private static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
            || path.Contains($"{sep}bin{sep}", StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

public partial class HostServerTests
{
    private static Task<CodeExcerptResult> ExcerptAsync(Harness h, string code, bool selection = false) =>
        h.Client.InvokeWithParameterObjectAsync<CodeExcerptResult>(
                "code/excerpt", new { code, fileName = "Big.cs", selection })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

    [Fact]
    public async Task CodeExcerpt_CutsAFileBeyondTheBudget_AndBothReadersLearnHowMuch()
    {
        using var h = CreateHarness();                     // the default window
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var r = await ExcerptAsync(h, ReadActionExcerptTests.Source(1_200));   // 36,000 characters

        Assert.True(r.Truncated);
        Assert.True(r.Text.Length <= CodeExcerpt.MaxChars + 120, $"{r.Text.Length} characters sent");
        Assert.Contains("of 1200 lines", r.Text, StringComparison.Ordinal);    // the model
        Assert.Contains("1200", r.Label, StringComparison.Ordinal);            // the human, under the question
        Assert.Contains("Big.cs", r.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeExcerpt_LeavesAFileThatFitsTheWindowWhole_AndItsLabelUntouched()
    {
        using var h = CreateHarness(cfg => cfg.ContextWindowSize = 32_768);
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var code = ReadActionExcerptTests.Source(650);

        var whole = await ExcerptAsync(h, code);
        Assert.False(whole.Truncated);
        Assert.Equal(code, whole.Text);
        Assert.Equal("Big.cs", whole.Label);

        Assert.Equal("Selection (Big.cs)", (await ExcerptAsync(h, code, selection: true)).Label);
    }
}
