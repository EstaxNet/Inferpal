using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An @-mention that attaches nothing says why — and an empty folder context is not an empty folder.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The rule was written twice in the files it is broken in. <c>mention/resolve</c> grew a
/// <c>Notice</c> field for the <c>@debugger</c> case, under the words "nothing to attach is not
/// nothing to say" — and the four other branches kept answering nulls, which the adapter shows as
/// <b>nothing at all</b>. Measured: an unknown category, <c>@folder</c> with no path and
/// <c>@code</c> with a blank query each came back <c>name=∅ content=∅ notice=∅</c>.
/// </para>
/// <para>
/// ⚠ <c>BuildFolderContext</c> opens on "Every cut is stated" and lists five of them. The sixth
/// empties the answer instead of narrowing it: a folder that cannot be listed produced
/// <c>"Folder: X\n\nFiles:\n\n"</c> — measured at 77 characters — which both front-ends attach
/// under a chip named after the folder. To the model that reads "this folder holds no source file".
/// </para>
/// </remarks>
public sealed class SilentMentionTests
{
    // ── An empty listing that means something else ────────────────────────────

    [Fact]
    public void AFolderThatCannotBeListed_IsNotReportedAsAFolderWithNoFiles()
    {
        var missing = Path.Combine(Path.GetTempPath(), "inferpal-gone-" + Guid.NewGuid().ToString("N")[..8]);

        var context = MentionController.BuildFolderContext(missing, CancellationToken.None);

        Assert.Contains("could not be listed", context);
        Assert.Contains("NOT a statement", context);
    }

    [Fact]
    public void AFolderTheWalkSkipsByName_SaysThatIsWhyItIsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferpal-skip-" + Guid.NewGuid().ToString("N")[..8], "node_modules");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "index.js"), "console.log(1);");

            var context = MentionController.BuildFolderContext(dir, CancellationToken.None);

            Assert.Contains("skips by name", context);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); } catch { } }
    }

    /// <summary>Reference arm: a folder that IS empty says nothing of the sort — it simply has no
    /// files, which is an answer about the folder.</summary>
    [Fact]
    public void AFolderThatIsGenuinelyEmpty_CarriesNoSuchNote()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferpal-empty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var context = MentionController.BuildFolderContext(dir, CancellationToken.None);

            Assert.DoesNotContain("could not be listed", context);
            Assert.DoesNotContain("skips by name", context);
            Assert.Contains("Files:", context);   // witness: the same shape, minus the note
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>Reference arm: a folder with files is unchanged — and still declares its own caps.</summary>
    [Fact]
    public void AFolderWithFiles_IsUnchanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferpal-full-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.cs"), "class A { }");

            var context = MentionController.BuildFolderContext(dir, CancellationToken.None);

            Assert.Contains("a.cs", context);
            Assert.Contains("class A", context);
            Assert.DoesNotContain("could not be listed", context);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ── The rule, where it was stated ─────────────────────────────────────────

    /// <summary>
    /// The VS Code adapter says a host-backed mention could not run, like every palette command
    /// beside it.
    /// </summary>
    /// <remarks>
    /// ⚠ A source scan, with its witness: this path needs a VS Code extension host, so the suite
    /// cannot execute it — same standing as <c>ArchiveFailureSilenceTests</c>. The measured case is
    /// the most common refusal there is: with no folder open the host never starts, and every
    /// host-backed mention did nothing at all.
    /// </remarks>
    [Fact]
    public void TheVsCodeMention_SaysItWhenTheHostIsNotRunning()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "vscode", "src", "chatViewProvider.ts"));

        // WITNESS: the branch this asserts on is really in this file.
        var branch = Regex.Match(
            src,
            @"private async resolveMention[\s\S]*?default: \{[\s\S]*?\n        \}",
            RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(branch.Success, "the host-backed mention branch was not found — the scan would pass on nothing");

        Assert.Contains("hostUnavailableMessage()", branch.Value);
        Assert.Contains("result.notice", branch.Value);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "vscode", "src")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
