using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code's <c>@</c> picker skips the folders Visual Studio's picker skips — the Core's
/// <see cref="WorkspaceScan.ExcludedDirNames"/> — and offers the open TABS first.
/// </summary>
/// <remarks>
/// Its exclusion glob was written by hand (<c>node_modules,bin,obj,out,dist,.git</c>): <c>@Program</c> offered
/// <c>.inferpal/history/…Program.cs.bak</c> snapshots and <c>.venv</c> files, which Visual Studio's picker never shows.
/// And its "open" files came from <c>workspace.textDocuments</c>, which also holds the documents loaded without a tab.
/// </remarks>
public sealed class MentionPickerExclusionsTests
{
    [Fact]
    public void ThePicker_ExcludesTheCoresFolders_ButTheOnesAGlobCannotJudge()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        var m = Regex.Match(provider, @"const PICKER_EXCLUDED_DIRS = \[([^\]]*)\];");
        Assert.True(m.Success, "PICKER_EXCLUDED_DIRS was not found: this test would have measured nothing.");
        var picker = Regex.Matches(m.Groups[1].Value, "'([^']+)'").Select(x => x.Groups[1].Value).ToHashSet();

        // packages: the Core excludes it only when it holds NuGet packages, by its content.
        var core = WorkspaceScan.ExcludedDirNames.Where(d => d != "packages").ToHashSet();
        Assert.Empty(core.Except(picker));
        // The one addition, named: a TypeScript project's compiled output.
        Assert.Equal(["out"], picker.Except(core));

        Assert.Contains("`**/{${PICKER_EXCLUDED_DIRS.join(',')}}/**`", provider, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePickersOpenFiles_AreTheTabs()
    {
        var body = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("chatViewProvider.ts"), "private async mentionSuggestions(");
        Assert.Contains("openTabPaths()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("textDocuments", body, StringComparison.Ordinal);
    }
}
