using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The workflow that publishes to the marketplaces holds no credential: each registry trusts the file, run in an
/// environment whose reviewer approves every run. What makes it safe is held here, read from the file itself.
/// </summary>
public sealed class PublishWorkflowTests
{
    private static string Workflow()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, ".github", "workflows", "publish.yml"));
    }

    /// <summary>Without the environment, a pushed tag publishes with nobody's approval.</summary>
    [Fact]
    public void ThePublishingJob_WaitsInTheEnvironmentItsReviewerApproves()
    {
        var workflow = Workflow();

        Assert.Matches(new Regex(@"^    environment: marketplaces\r?$", RegexOptions.Multiline), workflow);
        Assert.Matches(new Regex(@"^      id-token: write", RegexOptions.Multiline), workflow);
        // The workflow's own permissions grant no token: only the job in the environment gets one.
        var jobs = workflow.IndexOf("\njobs:", StringComparison.Ordinal);
        Assert.True(jobs > 0);
        Assert.DoesNotContain("id-token", workflow[..jobs], StringComparison.Ordinal);
    }

    /// <summary>A stored token would take precedence over the OIDC exchange (ovsx reads it first), and outlive it.</summary>
    [Fact]
    public void NoStoredToken_IsRead()
    {
        var workflow = Workflow();

        Assert.DoesNotContain("secrets.", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("VSCE_PAT", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("OVSX_PAT", workflow, StringComparison.Ordinal);
        Assert.Contains("publish --oidc", workflow, StringComparison.Ordinal);
        Assert.Contains("publish --trusted-publishing", workflow, StringComparison.Ordinal);
    }

    /// <summary>The tools run with a credential that publishes: a moving version is someone else's code, run later.</summary>
    [Fact]
    public void EveryPublishingTool_IsPinnedToAnExactVersion()
    {
        var tools = Regex.Matches(Workflow(), @"npx --yes (\S+)").Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(2, tools.Count);
        Assert.All(tools, tool => Assert.Matches(@"@\d+\.\d+\.\d+$", tool));
    }

    /// <summary>A tag is text someone else may have written: it reaches a script through <c>env:</c>, never spliced in.</summary>
    [Fact]
    public void NoScript_InterpolatesAnExpression()
    {
        var runs = Regex.Matches(Workflow(), @"^\s+run: \|?\r?\n?((?:\s{10,}.*\r?\n)*|.*)", RegexOptions.Multiline)
                        .Select(m => m.Value).ToList();

        Assert.NotEmpty(runs);
        Assert.All(runs, run => Assert.DoesNotContain("${{", run, StringComparison.Ordinal));
    }
}
