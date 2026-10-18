using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Inferpal.Services.Governance;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The ignore files' dialect is git's, judged by git itself: a corpus of patterns and paths, each verdict compared with
/// <c>git check-ignore --no-index -v -n</c> — which file and line decide, a folder that excludes what is under it, a
/// negation that re-includes, and one that cannot (under an excluded folder).
/// </summary>
public sealed class GitIgnoreDialectTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-ignore").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static IEnumerable<object[]> Corpora()
    {
        yield return
        [
            new[]
            {
                "# a comment", "", "*.log", "!important.log", "build/", "!build/keep.txt", "/src/gen/*", "!/src/gen/keep.cs",
                "secret*", "doc/**/*.pdf", "**/tmp", "a/**/b", "x/**", "[abc].txt", "[!a-c]?.md", "\\#hash", "\\!bang",
                "foo/bar", "*.min.*", "node_modules", "**/cache/", "logs/*.txt", "  # not a comment, a pattern with spaces",
                "[\\d]ef.txt",
            },
            new[]
            {
                "app.log", "important.log", "deep/dir/other.log", "deep/dir/important.log", "build/a.txt", "build/keep.txt",
                "build/sub/b.txt", "src/gen/x.cs", "src/gen/keep.cs", "src/gen/deeper/y.cs", "src/secret.txt", "secrets/x",
                "doc/a.pdf", "doc/x/y/z.pdf", "doc/x.txt", "tmp/a", "q/tmp/b", "a/b", "a/x/y/b", "a/x/b/c", "x/1", "x/y/2",
                "a.txt", "d.txt", "aa.md", "da.md", "ab.md", "#hash", "!bang", "foo/bar", "sub/foo/bar", "app.min.js",
                "app.js", "node_modules/p/index.js", "q/node_modules/r.js", "cache/x", "q/cache/y", "logs/a.txt",
                "logs/sub/b.txt", "other.cs", "src/main.cs", "def.txt", "1ef.txt",
            },
        ];
        yield return
        [
            new[] { "/root.txt", "mid/dle", "*/star", "!keep.me", "**", "!keep/**", "!*.keep" },
            new[] { "root.txt", "sub/root.txt", "mid/dle", "x/mid/dle", "a/star", "a/b/star", "keep/x", "keep.me", "b.keep" },
        ];
        yield return
        [
            new[] { "*.pem", "!public.pem", ".env*", "!.env.example", "config/secrets/", "**/private/**", "Generated*.cs", "?.tmp" },
            new[]
            {
                "a.pem", "certs/b.pem", "public.pem", "certs/public.pem", ".env", ".env.local", ".env.example", "app/.env",
                "config/secrets/k.json", "config/secrets/deep/k.json", "x/config/secrets/k.json", "private/a", "x/private/y/z",
                "privateer/a", "GeneratedModels.cs", "src/GeneratedModels.cs", "Generator.cs", "a.tmp", "ab.tmp",
            },
        ];
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryVerdict_IsGitsOwn(int corpus)
    {
        var data     = Corpora().ElementAt(corpus);
        var patterns = (string[])data[0];
        var paths    = (string[])data[1];
        Assert.True(Git("init", "-q", "."), "`git init` failed: UNDECIDED, not green.");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), string.Join("\n", patterns) + "\n");
        foreach (var p in paths)
        {
            var full = Path.Combine(_root, p.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }

        var git = CheckIgnore(paths);
        Assert.Equal(paths.Length, git.Count);   // WITNESS: git answered for every path
        var ours = GitIgnoreRules.Parse(".gitignore", File.ReadAllText(Path.Combine(_root, ".gitignore")), ignoreCase: false);

        var differences = new List<string>();
        foreach (var path in paths)
        {
            var (gitExcluded, gitLine) = git[path];
            var rule = ours.Judge(path, isFolder: false);
            if ((rule is not null) != gitExcluded || (gitExcluded && rule!.Line != gitLine))
                differences.Add($"{path}: git {(gitExcluded ? $"excludes (line {gitLine})" : "keeps")}, " +
                                $"ours {(rule is null ? "keeps" : $"excludes (line {rule.Line})")}");
        }
        Assert.True(differences.Count == 0, "Verdicts that differ from git's:\n" + string.Join("\n", differences));

        // ⚠ And through the judge the product asks — its folder walk and cache, its merge, the platform's case — with the
        // file read as an ignore file of a declared workspace.
        ContentExclusions.Declare(_root, () => ".gitignore");
        try
        {
            var throughTheWorkspace = paths.Where(path => (ContentExclusions.Of(Path.Combine(_root, path)) is not null) != git[path].Excluded)
                                           .ToList();
            Assert.True(throughTheWorkspace.Count == 0,
                "Verdicts of the workspace's judge that differ from git's:\n" + string.Join("\n", throughTheWorkspace));
        }
        finally { ContentExclusions.ForgetForTests(_root); }
        // Reference arm: the corpus discriminates — some paths are excluded and some are not.
        Assert.Contains(git.Values, v => v.Excluded);
        Assert.Contains(git.Values, v => !v.Excluded);
    }

    private Dictionary<string, (bool Excluded, int Line)> CheckIgnore(IEnumerable<string> paths)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            // ⚠ LF and no BOM: a CRLF line is read as a path ending in \r, and git answers about another file.
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var a in (string[])["-c", "core.ignorecase=false", "-c", "core.quotePath=false", "check-ignore", "--no-index", "-v", "-n", "--stdin"])
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        foreach (var path in paths) p.StandardInput.Write(path + "\n");
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);

        // "<source>:<line>:<pattern>\t<path>", or "::\t<path>" when no rule matched.
        var verdicts = new Dictionary<string, (bool, int)>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            var (rule, path) = (line[..tab], line[(tab + 1)..].TrimEnd('\r'));
            if (rule == "::") { verdicts[path] = (false, 0); continue; }
            var first = rule.IndexOf(':');
            var second = rule.IndexOf(':', first + 1);
            var number = int.Parse(rule[(first + 1)..second], System.Globalization.CultureInfo.InvariantCulture);
            var pattern = rule[(second + 1)..];
            verdicts[path] = (!pattern.StartsWith('!'), number);
        }
        return verdicts;
    }

    private bool Git(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
