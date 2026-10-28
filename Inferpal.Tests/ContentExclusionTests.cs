using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Governance;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Presentation;
using Inferpal.Services.Prompting;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the workspace's ignore files exclude reaches nothing that reads for the model — and every exclusion is SAID.
/// </summary>
/// <remarks>
/// ⚠ The property, not a list of readers: a file holding a token nobody else holds, excluded by <c>.aiignore</c>, and
/// every reading path asked about it — the token never comes out, and each refusal names the file and the line. The
/// reference arm runs the same paths WITHOUT the ignore file: the token comes out of each, or the test measures nothing.
/// <para>⚠ Alone (<see cref="ContentExclusionSerialCollection"/>): Inferpal's backups are unread while ANY declared root
/// excludes something, so the reference arm's backup read measures the whole process — run beside another class that
/// declares a root with an ignore file, it was refused for a reason this test does not set.</para>
/// </remarks>
[Collection(ContentExclusionSerialCollection.Name)]
public sealed class ContentExclusionTests : IDisposable
{
    private const string Marker = "MARKER_7f3a9c51";
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-exclude").FullName;

    public void Dispose()
    {
        ContentExclusions.ForgetForTests(_root);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>secret/vault.cs</c> and <c>keys.txt</c> hold the token; <c>.aiignore</c> (when <paramref name="excluded"/>)
    /// excludes them, and a negation tries — in vain, as in git — to take back a file under the excluded folder.
    /// </summary>
    private void Workspace(bool excluded)
    {
        if (excluded)
            File.WriteAllText(Path.Combine(_root, ".aiignore"), "# secrets\nsecret/\nkeys.txt\n!secret/allowed.cs\nsrc/Hidden.cs\n");
        Directory.CreateDirectory(Path.Combine(_root, "secret"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "secret", "vault.cs"), SampleClass("Vault", Marker));
        File.WriteAllText(Path.Combine(_root, "secret", "allowed.cs"), SampleClass("Allowed", Marker));
        File.WriteAllText(Path.Combine(_root, "keys.txt"), $"api-key = {Marker}\n");
        File.WriteAllText(Path.Combine(_root, "src", "App.cs"), SampleClass("App", "nothing to hide"));
        // Excluded by itself, its test file not: the source alone decides.
        File.WriteAllText(Path.Combine(_root, "src", "Hidden.cs"), SampleClass("Hidden", Marker));
        ContentExclusions.Declare(_root, () => ContentExclusions.DefaultFiles);
    }

    private static string SampleClass(string name, string value) => string.Join('\n',
        $"public class {name}",
        "{",
        $"    public const string Value = \"{value}\";",
        "    public int One()   => 1;",
        "    public int Two()   => 2;",
        "    public int Three() => 3;",
        "    public int Four()  => 4;",
        "    public int Five()  => 5;",
        "}") + "\n";

    private string Vault => Path.Combine(_root, "secret", "vault.cs");
    private string Keys  => Path.Combine(_root, "keys.txt");

    private ToolRegistry Registry(IEditorSurface? editor = null, InferpalConfig? config = null)
    {
        config ??= new InferpalConfig();
        var client = new FakeInferenceProvider();
        editor ??= new NullEditorSurface();
        var approval = new NoopApproval();
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        index.SetRoot(_root);
        return new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                new McpToolService(config, approval), new DocsIndexService(client, config),
                                new OpenDocumentOverlay(), new NullDebugSession());
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    // ── the tools ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnExcludedFile_IsNeverTheSourceOfARestore(bool excluded)
    {
        Workspace(excluded);
        var result = await Registry().ExecuteAsync("restore_file",
            Args(new { path = "src/App.cs", snapshot = "keys.txt" }), default);
        var restored = File.ReadAllText(Path.Combine(_root, "src", "App.cs"));

        if (!excluded)
        {
            // Reference arm: another workspace file as the source is copied — the arm the exclusion has to stop.
            Assert.Contains(Marker, restored);
            return;
        }
        Assert.Contains(".aiignore, line 3", result);
        Assert.DoesNotContain(Marker, restored);
    }

    [Fact]
    public void AUrl_IsNotTheFileItsPathEndsLike_UnlessItLeavesItsScheme()
    {
        Workspace(excluded: true);
        // A page to fetch whose path ends like an excluded file is not that file (fetch_url is not refused)…
        Assert.Null(ToolRegistry.ExcludedNamedFile(Args(new { url = "https://example.com/keys.txt" }), _root));
        // …one that leaves its scheme folder when resolved as a path is.
        Assert.NotNull(ToolRegistry.ExcludedNamedFile(Args(new { url = "ab://../keys.txt" }), _root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheTools_NeverShowAnExcludedFile_AndSayWhy(bool excluded)
    {
        Workspace(excluded);
        var tools = Registry();

        var read   = await tools.ExecuteAsync("read_file", Args(new { path = Vault }), default);
        var keys   = await tools.ExecuteAsync("read_file", Args(new { path = "keys.txt" }), default);
        var search = await tools.ExecuteAsync("search_in_files", Args(new { path = _root, pattern = Marker }), default);
        var listed = await tools.ExecuteAsync("list_files", Args(new { path = _root, recursive = true }), default);

        if (!excluded)
        {
            // Reference arm: the same calls read the token.
            Assert.Contains(Marker, read);
            Assert.Contains(Marker, keys);
            Assert.Contains(Marker, search);
            Assert.Contains("vault.cs", listed);
            // The two spellings the excluded arm refuses do read the file: refused there for the file, not the spelling.
            Assert.Contains(Marker, await tools.ExecuteAsync("read_file", Args(new { path = "ab://../keys.txt" }), default));
            Assert.Contains(Marker, await tools.ExecuteAsync("read_file",
                Args(new { path = string.Concat(Enumerable.Repeat("a/../", 1000)) + "keys.txt" }), default));
            var refBackup = Path.Combine(_root, ".inferpal", "history", "20261018_000000_1a2b3c4d_keys.txt.bak");
            Directory.CreateDirectory(Path.GetDirectoryName(refBackup)!);
            File.WriteAllText(refBackup, Marker);
            // The rules of a root deleted by a class that ran before are re-read on their next recheck: until then the
            // process still "excludes something", and the backup read would measure that, not this arm.
            var settled = Stopwatch.StartNew();
            while (ContentExclusions.AnyRules() && settled.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100);
            Assert.False(ContentExclusions.AnyRules(),
                "UNDECIDED, not green: another declared root still has rules, so the reference backup read cannot be measured.");
            Assert.Contains(Marker, await tools.ExecuteAsync("read_file", Args(new { path = refBackup }), default));
            return;
        }
        Assert.DoesNotContain(Marker, read + keys + search + listed);
        Assert.Contains(".aiignore, line 2 (`secret/`)", read);
        Assert.Contains(".aiignore, line 3 (`keys.txt`)", keys);
        Assert.Contains("excluded from AI tools were skipped", search);
        Assert.DoesNotContain("vault.cs", listed);
        Assert.Contains("excluded from AI tools were skipped", listed);
        // A file under an excluded folder cannot be taken back by a negation — git's rule.
        Assert.Contains(".aiignore, line 2", await tools.ExecuteAsync("read_file",
            Args(new { path = Path.Combine(_root, "secret", "allowed.cs") }), default));
        // A path written with a trailing newline or a leading space is the file the tool reads: refused alike.
        Assert.Contains(".aiignore, line 3", await tools.ExecuteAsync("read_file", Args(new { path = "keys.txt\n" }), default));
        Assert.Contains(".aiignore, line 3", await tools.ExecuteAsync("read_file", Args(new { path = " keys.txt" }), default));
        // Written so a look at the TEXT misses it, resolved to the same file: a "scheme" its `..` leaves, a path too long.
        Assert.Contains(".aiignore, line 3", await tools.ExecuteAsync("read_file", Args(new { path = "ab://../keys.txt" }), default));
        Assert.Contains(".aiignore, line 3", await tools.ExecuteAsync("read_file",
            Args(new { path = string.Concat(Enumerable.Repeat("a/../", 1000)) + "keys.txt" }), default));
        // The 70th edit of a batch is seen as well as the first.
        var edits = Enumerable.Range(0, 69)
            .Select(i => (object)new { path = "src/App.cs", old_content = $"absent {i}", new_content = "x" })
            .Append(new { path = "keys.txt", old_content = "api-key", new_content = "x" }).ToArray();
        Assert.Contains(".aiignore, line 3", await tools.ExecuteAsync("apply_edits", Args(new { edits }), default));
        Assert.Contains(Marker, File.ReadAllText(Keys));
        // A search for an excluded file's NAME is not a read of it: it runs, and says what it skipped.
        var byName = await tools.ExecuteAsync("search_in_files", Args(new { path = _root, pattern = "keys.txt" }), default);
        Assert.DoesNotContain("Error:", byName);
        Assert.DoesNotContain(Marker, byName);
        // Inferpal's backups may hold an excluded file under a name no rule can judge: unread while anything is excluded.
        var backup = Path.Combine(_root, ".inferpal", "history", "20261018_000000_1a2b3c4d_keys.txt.bak");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, Marker);
        var readBackup = await tools.ExecuteAsync("read_file", Args(new { path = backup }), default);
        Assert.DoesNotContain(Marker, readBackup);
        Assert.Contains("backup copy kept by Inferpal", readBackup);
        // And the visible file is still read.
        Assert.Contains("class App", await tools.ExecuteAsync("read_file", Args(new { path = "src/App.cs" }), default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFileCreatedWhereTheIgnoreFilesExclude_IsNotWritten(bool excluded)
    {
        // A write that creates a file is a change to it: `secret/` excluded, `secret/new.cs` is not created there.
        Workspace(excluded);
        var tools = Registry();
        var created = Path.Combine(_root, "secret", "new.cs");
        var said = await tools.ExecuteAsync("write_file", Args(new { path = "secret/new.cs", content = "class New { }\n" }), default);
        if (!excluded)
        {
            Assert.True(File.Exists(created), said);   // reference arm: the same call writes it
            return;
        }
        Assert.False(File.Exists(created));
        Assert.Contains(".aiignore, line 2 (`secret/`)", said);
        // A search for that path is not a write of it: it runs (the walk still says what it skipped under src/).
        var search = await tools.ExecuteAsync("search_in_files", Args(new { path = "src", pattern = "secret/new.cs" }), default);
        Assert.DoesNotContain("line 2", search);
        Assert.DoesNotContain("Error:", search);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheActiveDocument_IsNeitherReadNorChanged_WhenExcluded(bool excluded)
    {
        Workspace(excluded);
        var editor = new ActiveEditor(new ActiveDocument(Keys, File.ReadAllText(Keys)));
        var tools  = Registry(editor);

        var shown    = await tools.ExecuteAsync("get_active_document", Args(new { }), default);
        var inserted = await tools.ExecuteAsync("insert_at_cursor", Args(new { text = "more" }), default);

        if (!excluded)
        {
            Assert.Contains(Marker, shown);
            Assert.Equal("more", editor.Inserted);
            return;
        }
        Assert.DoesNotContain(Marker, shown);
        Assert.Contains("excluded from AI tools by .aiignore, line 3", shown);
        Assert.Contains("excluded from AI tools by .aiignore, line 3", inserted);
        Assert.Null(editor.Inserted);
    }

    // ── the readers outside the tools ───────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MentionsPinsAndCodeActions_NeverCarryAnExcludedFile(bool excluded)
    {
        Workspace(excluded);

        var folder = MentionController.BuildFolderContext(_root, default);
        var fix    = FixPromptBuilder.Build($"{Vault}(3,5): error CS0001: broken");
        var prompt = new SystemPromptBuilder(new InferpalConfig { PinnedContextFiles = Keys }).Build("BASE");
        var plan   = await TestGenerationPlanner.PlanAsync(new FakeInferenceProvider(), "m", Vault, File.ReadAllText(Vault), default);

        if (!excluded)
        {
            Assert.Contains(Marker, folder);
            Assert.Contains(Marker, fix);
            Assert.Contains(Marker, prompt);
            Assert.Null(MentionController.FileRefusal(Keys, _root));
            Assert.Null(MentionController.FolderRefusal(Path.Combine(_root, "secret"), _root));
            Assert.DoesNotContain(".aiignore", plan.Refusal ?? string.Empty);   // the fake answered nothing: another refusal
            return;
        }
        Assert.DoesNotContain(Marker, folder + fix + prompt);
        Assert.Contains(".aiignore line 2 (`secret/`)", folder);
        Assert.Contains("excluded from AI tools by", folder);
        Assert.Contains("excluded from AI tools by .aiignore line 2 (`secret/`)", fix);
        Assert.Contains("excluded from AI tools by .aiignore, line 3", prompt);
        // To the person: the file, the ignore file, the line and the pattern.
        var notice = MentionController.FileRefusal(Keys, _root);
        Assert.NotNull(notice);
        Assert.Contains("keys.txt", notice);
        Assert.Contains(".aiignore", notice);
        Assert.Contains("`keys.txt`", notice);
        Assert.NotNull(MentionController.FolderRefusal(Path.Combine(_root, "secret"), _root));
        Assert.False(plan.Ok);
        Assert.Contains(".aiignore", plan.Refusal);
        var hidden = Path.Combine(_root, "src", "Hidden.cs");
        Assert.Null(ContentExclusions.Of(TestFilePathResolver.Resolve(hidden)));   // WITNESS: its test file is not excluded
        var alone = await TestGenerationPlanner.PlanAsync(new FakeInferenceProvider(), "m", hidden, File.ReadAllText(hidden), default);
        Assert.Contains("`src/Hidden.cs`", alone.Refusal);   // the pattern of line 5, in any interface language
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckAndCommit_SayWhatTheModelDidNotRead(bool excluded)
    {
        Workspace(excluded);
        Directory.CreateDirectory(Path.Combine(_root, ".git"));   // the work tree the diff's paths are relative to
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal", "checks"));
        File.WriteAllText(Path.Combine(_root, ".inferpal", "checks", "style.md"), "# Style\nNo magic numbers.\n");
        var diff = string.Join("\n",
            "diff --git a/secret/vault.cs b/secret/vault.cs",
            "index 1111111..2222222 100644",
            "--- a/secret/vault.cs",
            "+++ b/secret/vault.cs",
            "@@ -1,1 +1,1 @@",
            $"+    public const string Value = \"{Marker}\";",
            "diff --git a/src/App.cs b/src/App.cs",
            "index 3333333..4444444 100644",
            "--- a/src/App.cs",
            "+++ b/src/App.cs",
            "@@ -1,1 +1,1 @@",
            "+    public int Six() => 6;",
            "");
        GitRunner git = (args, _) => Task.FromResult(args switch
        {
            "diff --staged"              => (diff, 0),
            "rev-parse --show-toplevel"  => (_root + "\n", 0),
            _                            => (string.Empty, 0),
        });
        var sent   = new List<string>();
        var client = new FakeInferenceProvider
        {
            ChatResult = new ChatTurnResult("No finding.", null, 0, 0),
            OnChatRequest = (_, history, _, _) =>
            {
                sent.AddRange(history.Select(m => m.Content ?? string.Empty));
                return Task.FromResult(new ChatTurnResult("No finding.", null, 0, 0));
            },
            AgentRunResult = new AgentResult("feat: add Six", [], []),
        };

        var check  = await CheckCommandHandler.HandleAsync(client, new InferpalConfig(), _root, ["/check"], git, null, default);
        var commit = await CommitCommandHandler.ProposeAsync(client, new InferpalConfig(), git, null, default);
        var toModel = string.Join("\n", sent.Concat(client.AgentRuns.SelectMany(r => r.History).Select(m => m.Content ?? "")));

        Assert.Contains("Six", toModel);   // WITNESS: the visible change reached the model both times
        if (!excluded)
        {
            Assert.Contains(Marker, toModel);
            return;
        }
        Assert.DoesNotContain(Marker, toModel);
        Assert.Contains("excluded from AI tools were skipped", toModel);
        // To the person, above the verdict and before the proposal it pre-fills.
        Assert.Contains(".aiignore", check.Message);
        Assert.Contains(".aiignore", commit.Notice);
    }

    /// <summary>A workspace opened through a link (a junction, a linked home folder, macOS's <c>/var</c>) — to the whole
    /// repository, or to a folder of it: git names its paths with the links resolved, the workspace keeps the spelling it
    /// was opened by, and the excluded file is still recognised in the diff /check and /commit give the model.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("src")]
    public async Task AWorkspaceOpenedThroughALink_KeepsItsExcludedFilesOutOfTheDiff(string linkedFolder)
    {
        Workspace(excluded: true);
        File.WriteAllText(Path.Combine(_root, "src", ".aiignore"), "Hidden.cs\n");   // the linked folder's own rules
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        ContentExclusions.ForgetForTests(_root);   // only the linked spelling is declared, as the editor opened it
        var outside = Directory.CreateTempSubdirectory("inferpal-exclude-link").FullName;
        var linked  = Path.Combine(outside, "ws");
        try
        {
            // Creating a link needs Developer Mode or elevation on Windows: without it, nothing to measure.
            try { Directory.CreateSymbolicLink(linked, Path.Combine(_root, linkedFolder)); }
            catch (Exception) { return; }
            ContentExclusions.Declare(linked, () => ContentExclusions.DefaultFiles);
            var (file, other) = linkedFolder.Length == 0 ? ("secret/vault.cs", "src/App.cs") : ("src/Hidden.cs", "src/App.cs");
            var diff = string.Join("\n",
                $"diff --git a/{file} b/{file}",
                $"--- a/{file}",
                $"+++ b/{file}",
                "@@ -1,1 +1,1 @@",
                $"+    public const string Value = \"{Marker}\";",
                $"diff --git a/{other} b/{other}",
                $"--- a/{other}",
                $"+++ b/{other}",
                "@@ -1,1 +1,1 @@",
                "+    public int Six() => 6;",
                "");
            GitRunner git = (args, _) => Task.FromResult(args switch
            {
                "diff --staged"             => (diff, 0),
                "rev-parse --show-toplevel" => (_root + "\n", 0),   // the link resolved, as git names it
                _                           => (string.Empty, 0),
            });
            var sent   = new List<string>();
            var client = new FakeInferenceProvider
            {
                OnChatRequest = (_, history, _, _) =>
                {
                    sent.AddRange(history.Select(m => m.Content ?? string.Empty));
                    return Task.FromResult(new ChatTurnResult("No finding.", null, 0, 0));
                },
                AgentRunResult = new AgentResult("feat: add Six", [], []),
            };

            var check   = await CheckCommandHandler.HandleAsync(client, new InferpalConfig(), linked, ["/check"], git, null, default);
            var commit  = await CommitCommandHandler.ProposeAsync(client, new InferpalConfig(), git, null, default);
            var toModel = string.Join("\n", sent.Concat(client.AgentRuns.SelectMany(r => r.History).Select(m => m.Content ?? "")));

            Assert.Contains("Six", toModel);   // WITNESS: the visible change reached the model both times
            Assert.DoesNotContain(Marker, toModel);
            Assert.Contains(".aiignore", check.Message);
            Assert.Contains(".aiignore", commit.Notice);
        }
        finally
        {
            ContentExclusions.ForgetForTests(linked);
            try { Directory.Delete(linked); } catch { }   // the link itself, never what it points to
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GitStatus_LeavesTheExcludedFilesOutOfItsDiff(bool excluded)
    {
        Workspace(excluded);
        Assert.True(Git("init", "-q", ".") && Git("config", "user.email", "t@example.invalid") && Git("config", "user.name", "t")
                    && Git("add", "-A") && Git("commit", "-q", "-m", "seed"), "git failed: UNDECIDED, not green.");
        // A user's configuration that reshapes a diff: colours, mnemonic prefixes, paths relative to the current folder.
        Assert.True(Git("config", "color.ui", "always") && Git("config", "diff.mnemonicPrefix", "true")
                    && Git("config", "diff.relative", "true"), "git config failed: UNDECIDED, not green.");
        File.WriteAllText(Vault, SampleClass("Vault", Marker + "_CHANGED"));
        File.WriteAllText(Path.Combine(_root, "src", "App.cs"), SampleClass("App", "visible change"));

        var report = await Registry().ExecuteAsync("get_git_status", Args(new { include_diff = true }), default);

        Assert.Contains("visible change", report);   // WITNESS: the diff is there
        if (!excluded)
        {
            Assert.Contains(Marker + "_CHANGED", report);
            return;
        }
        Assert.DoesNotContain(Marker, report);
        Assert.Contains("excluded from AI tools were skipped", report);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheIndex_NeitherHoldsNorReturnsAnExcludedFile(bool excluded)
    {
        Workspace(excluded);
        var config = new InferpalConfig { RagEnabled = true };
        using var index = new ProjectIndexService(new FakeInferenceProvider(), config, new LspSemanticProvider());
        index.SetRoot(_root);
        index.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((index.IsIndexing || index.ChunkCount == 0) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(index.ChunkCount > 0, "the pass did not run: UNDECIDED, not green. " + index.Status);

        var hits = await index.SearchAsync(null, Marker, 10, default);

        if (!excluded)
        {
            Assert.Contains(hits, h => h.Chunk.FilePath.EndsWith("vault.cs", StringComparison.Ordinal));
            Assert.Equal(0, index.ExcludedByIgnoreFiles.Count);
            return;
        }
        Assert.DoesNotContain(hits, h => h.Chunk.Content.Contains(Marker, StringComparison.Ordinal));
        Assert.Equal(3, index.ExcludedByIgnoreFiles.Count);   // vault.cs, allowed.cs and Hidden.cs; keys.txt is not source
        Assert.Contains(".aiignore line 2", index.ExcludedByIgnoreFiles.Rules);
    }

    /// <summary>
    /// An index built before the ignore file (a saved one read at start, a rule added while the re-index waits) holds the
    /// file's chunks: the search never returns them, and the C# index lets the file go.
    /// </summary>
    [Fact]
    public async Task AnIndexBuiltBeforeTheIgnoreFile_NoLongerAnswersWithWhatItNowExcludes()
    {
        Workspace(excluded: false);
        using var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig { RagEnabled = true },
                                                  new LspSemanticProvider());
        index.SetRoot(_root);
        index.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((index.IsIndexing || index.ChunkCount == 0) && DateTime.UtcNow < deadline) await Task.Delay(50);
        var csharp = CSharpSemanticIndex.ForWorkspace(_root);
        csharp.Build();
        // WITNESS: before the ignore file, both indexes hold the file.
        Assert.Contains(await index.SearchAsync(null, Marker, 10, default), h => h.Chunk.FilePath.EndsWith("vault.cs", StringComparison.Ordinal));
        Assert.Equal(4, csharp.FileCount);

        File.WriteAllText(Path.Combine(_root, ".aiignore"), "secret/\n");
        Assert.True(ContentExclusions.Changed(Path.Combine(_root, ".aiignore")));
        CSharpSemanticIndex.NotifyExclusionsChanged(_root);

        Assert.DoesNotContain(await index.SearchAsync(null, Marker, 10, default),
                              h => h.Chunk.FilePath.Contains("secret", StringComparison.Ordinal));
        Assert.Equal(2, csharp.FileCount);   // App.cs and Hidden.cs
    }

    /// <summary>
    /// An ignore file written while the workspace is open is followed: the C# index lets the files it excludes go, with no
    /// call from anyone — the watcher of the root's own files sees it, whatever its extension.
    /// </summary>
    [Fact]
    public async Task AnIgnoreFileWrittenWhileTheWorkspaceIsOpen_IsFollowed()
    {
        Workspace(excluded: false);
        using var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig(), new LspSemanticProvider());
        index.SetRoot(_root);   // arms the watchers, RAG on or off
        var csharp = CSharpSemanticIndex.ForWorkspace(_root);
        csharp.Build();
        Assert.Equal(4, csharp.FileCount);   // WITNESS

        File.WriteAllText(Path.Combine(_root, ".aiignore"), "secret/\n");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (csharp.FileCount != 2 && DateTime.UtcNow < deadline) await Task.Delay(50);

        Assert.Equal(2, csharp.FileCount);
    }

    /// <summary>
    /// A rename walks the workspace at every call: what the ignore files keep out of it is said every time, never only
    /// by the call that built the cached C# index.
    /// </summary>
    [Fact]
    public async Task ARename_SaysWhatItCouldNotReach_AtEveryCall()
    {
        Workspace(excluded: true);
        var tools = Registry();

        var first  = await tools.ExecuteAsync("rename_symbol", Args(new { old_name = "Three", new_name = "Trois", root = _root }), default);
        var second = await tools.ExecuteAsync("rename_symbol", Args(new { old_name = "Trois", new_name = "Three", root = _root }), default);

        Assert.Contains("excluded from AI tools were skipped", first);
        Assert.Contains("excluded from AI tools were skipped", second);
        Assert.Contains("Three()", File.ReadAllText(Vault));   // the excluded file is never changed
    }

    /// <summary>
    /// A workspace nested in another — a solution in a subfolder opened before the outer one, two ACP threads — has no
    /// ignore file of its own: the outer workspace's rules still keep its files out.
    /// </summary>
    [Fact]
    public void ANestedWorkspace_KeepsTheOuterWorkspacesExclusions()
    {
        File.WriteAllText(Path.Combine(_root, ".aiignore"), "tools/secrets/\n");
        var inner = Path.Combine(_root, "tools");
        Directory.CreateDirectory(Path.Combine(inner, "secrets"));
        ContentExclusions.Declare(inner, () => ContentExclusions.DefaultFiles);   // declared first, the closest root
        ContentExclusions.Declare(_root, () => ContentExclusions.DefaultFiles);
        try
        {
            Assert.NotNull(ContentExclusions.Of(Path.Combine(inner, "secrets", "k.json")));
            Assert.Null(ContentExclusions.Of(Path.Combine(inner, "other.json")));   // WITNESS: not everything
        }
        finally { ContentExclusions.ForgetForTests(inner); }
    }

    /// <summary><c>/read</c> of an excluded file is said to the person, translated, in place of a chip that would carry
    /// the model's refusal under the file's name.</summary>
    [Fact]
    public void ReadingAnExcludedFileByCommand_IsSaid_NotAttached()
    {
        Workspace(excluded: true);
        var excluded = Assert.IsType<SlashToolAction>(SlashCommandRouter.Route("/read keys.txt", []));
        var visible  = Assert.IsType<SlashToolAction>(SlashCommandRouter.Route("/read src/App.cs", []));

        Assert.Contains("`keys.txt`", excluded.Excluded(_root));
        Assert.Null(visible.Excluded(_root));
    }

    /// <summary>A rule written while the index is open is counted again for search_codebase — and only the files whose
    /// verdict changed are queued for the re-index.</summary>
    [Fact]
    public async Task ARuleWrittenWhileTheIndexIsOpen_IsCountedAgain_AndQueuesOnlyWhatChanged()
    {
        Workspace(excluded: false);
        using var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig { RagEnabled = true },
                                                  new LspSemanticProvider());
        index.SetRoot(_root);
        index.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((index.IsIndexing || index.ChunkCount == 0) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(0, index.ExcludedByIgnoreFiles.Count);   // WITNESS

        File.WriteAllText(Path.Combine(_root, ".aiignore"), "secret/\n");
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (index.ExcludedByIgnoreFiles.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);

        Assert.Equal(2, index.ExcludedByIgnoreFiles.Count);   // vault.cs and allowed.cs
        Assert.All(index.NotYetReindexed, f => Assert.Contains("secret", f, StringComparison.Ordinal));
    }

    /// <summary>Inferpal's backups are named as what they are — never as an ignore file's line 0.</summary>
    [Fact]
    public void BackupsKeptUnread_AreNamedAsBackups()
    {
        File.WriteAllText(Path.Combine(_root, ".aiignore"), "keys.txt\n");
        ContentExclusions.Declare(_root, () => ContentExclusions.DefaultFiles);
        var backup = Path.Combine(_root, ".inferpal", "history", "20261018_000000_1a2b3c4d_keys.txt.bak");
        var tally = new ContentExclusions.Tally();
        tally.Add(backup, ContentExclusions.Of(backup)!);

        Assert.DoesNotContain("line 0", tally.Rules());
        Assert.Contains("backups", tally.Rules());
        Assert.DoesNotContain(", 0", ContentExclusions.NoticeFor(backup, _root));
    }

    /// <summary>/check never reads a new file the workspace excludes: it takes none of the review's room, and is said.</summary>
    [Fact]
    public async Task Check_NeverReadsAnExcludedNewFile()
    {
        Workspace(excluded: true);
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal", "checks"));
        File.WriteAllText(Path.Combine(_root, ".inferpal", "checks", "style.md"), "# Style\nNo magic numbers.\n");
        // Larger than the review's room: read, it would leave no room for the visible file listed after it.
        File.WriteAllText(Path.Combine(_root, "secret", "new.cs"),
                          string.Join("\n", Enumerable.Range(0, 3000).Select(i => $"// line {i} {Marker}")));
        File.WriteAllText(Path.Combine(_root, "src", "New.cs"), SampleClass("NewVisible", "visible"));
        GitRunner git = (args, _) => Task.FromResult(args switch
        {
            "status --short" => ("?? secret/new.cs\n?? src/New.cs\n", 0),
            _ when args.StartsWith("ls-files", StringComparison.Ordinal) => ("secret/new.cs\nsrc/New.cs\n", 0),
            _ => (string.Empty, 0),
        });
        var sent = new List<string>();
        var client = new FakeInferenceProvider
        {
            OnChatRequest = (_, history, _, _) =>
            {
                sent.AddRange(history.Select(m => m.Content ?? string.Empty));
                return Task.FromResult(new ChatTurnResult("No finding.", null, 0, 0));
            },
        };

        var check = await CheckCommandHandler.HandleAsync(client, new InferpalConfig(), _root, ["/check"], git, null, default);
        var toModel = string.Join("\n", sent);

        Assert.Contains("NewVisible", toModel);   // WITNESS: the visible new file is reviewed
        Assert.DoesNotContain(Marker, toModel);
        Assert.Contains(".aiignore", check.Message);
    }

    // ── the dialect's edges ─────────────────────────────────────────────────────

    [Fact]
    public void WhatIsWrittenForTheAgent_IsNeverExcluded()
    {
        File.WriteAllText(Path.Combine(_root, ".aiignore"), "*.md\n.inferpal/\n.github/\n**/SKILL.md\n");
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal"));
        Directory.CreateDirectory(Path.Combine(_root, ".github", "skills", "deploy"));
        foreach (var f in new[] { "AGENTS.md", "CLAUDE.md", ".inferpal/context.md", ".github/copilot-instructions.md",
                                  ".github/skills/deploy/SKILL.md", "README.md" })
            File.WriteAllText(Path.Combine(_root, f), "x");
        ContentExclusions.Declare(_root, () => ContentExclusions.DefaultFiles);

        foreach (var f in new[] { "AGENTS.md", "CLAUDE.md", ".inferpal/context.md", ".github/copilot-instructions.md",
                                  ".github/skills/deploy/SKILL.md" })
            Assert.True(ContentExclusions.Of(Path.Combine(_root, f)) is null, f + " is written for the agent: never excluded.");
        Assert.NotNull(ContentExclusions.Of(Path.Combine(_root, "README.md")));   // WITNESS: the pattern does match
    }

    [Fact]
    public void SeveralIgnoreFiles_EachKeepItsNegations_AndTheSettingChoosesWhichAreRead()
    {
        File.WriteAllText(Path.Combine(_root, ".aiignore"), "*.log\n!keep.log\n");
        File.WriteAllText(Path.Combine(_root, ".cursorignore"), "dist/\n");
        string? setting = null;
        ContentExclusions.Declare(_root, () => setting);

        Assert.Equal(".aiignore", ContentExclusions.Of(Path.Combine(_root, "app.log"))?.File);
        Assert.Null(ContentExclusions.Of(Path.Combine(_root, "keep.log")));
        Assert.Equal(".cursorignore", ContentExclusions.Of(Path.Combine(_root, "dist", "a.js"))?.File);

        // The setting names the files read: empty reads none — once the workspace is declared again (a settings save).
        setting = string.Empty;
        ContentExclusions.Declare(_root, () => setting);
        Assert.Null(ContentExclusions.Of(Path.Combine(_root, "app.log")));
    }

    [Fact]
    public void AnIgnoreFileChanged_IsReadAgain()
    {
        var ignore = Path.Combine(_root, ".aiignore");
        File.WriteAllText(ignore, "a.txt\n");
        ContentExclusions.Declare(_root, () => ContentExclusions.DefaultFiles);
        Assert.NotNull(ContentExclusions.Of(Path.Combine(_root, "a.txt")));

        File.WriteAllText(ignore, "b.txt\n");
        File.SetLastWriteTimeUtc(ignore, DateTime.UtcNow.AddMinutes(1));
        Assert.True(ContentExclusions.Changed(ignore));   // the watcher's call: an ignore file of a declared root

        Assert.Null(ContentExclusions.Of(Path.Combine(_root, "a.txt")));
        Assert.NotNull(ContentExclusions.Of(Path.Combine(_root, "b.txt")));
        Assert.False(ContentExclusions.Changed(Path.Combine(_root, "a.txt")));   // any other file is not one
    }

    [Fact]
    public void AFileOutsideEveryDeclaredWorkspace_IsNotJudged()
    {
        File.WriteAllText(Path.Combine(_root, ".aiignore"), "*\n");
        ContentExclusions.Declare(Path.Combine(_root, "inner"), () => ContentExclusions.DefaultFiles);
        Directory.CreateDirectory(Path.Combine(_root, "inner"));
        try
        {
            // The ignore file sits above the declared root: only a root's own files are read.
            Assert.Null(ContentExclusions.Of(Path.Combine(_root, "inner", "x.cs")));
            Assert.Null(ContentExclusions.Of(Path.Combine(_root, "x.cs")));
        }
        finally { ContentExclusions.ForgetForTests(Path.Combine(_root, "inner")); }
    }

    [Fact]
    public void ADiff_LosesTheSectionsOfExcludedFiles_RenamesAndQuotedNamesIncluded()
    {
        // Anchored: an unanchored folder rule matches i/secret/ too, and would hide the prefix question.
        File.WriteAllText(Path.Combine(_root, ".aiignore"), "/secret/\n");
        ContentExclusions.Declare(_root, () => ContentExclusions.DefaultFiles);
        var diff = string.Join("\n",
            "diff --git a/old.cs b/secret/new.cs",
            "similarity index 90%",
            "rename from old.cs",
            "rename to secret/new.cs",
            "@@ -1 +1 @@",
            "+" + Marker,
            "diff --git \"a/secret/with space.cs\" \"b/secret/with space.cs\"",
            "--- \"a/secret/with space.cs\"",
            "+++ \"b/secret/with space.cs\"",
            "@@ -1 +1 @@",
            "+" + Marker,
            "diff --git a/kept.cs b/kept.cs",
            "--- a/kept.cs",
            "+++ b/kept.cs",
            "@@ -1 +1 @@",
            "+visible",
            "diff --git i/secret/m.cs w/secret/m.cs",
            "--- i/secret/m.cs",
            "+++ w/secret/m.cs",
            "@@ -1 +1 @@",
            "+" + Marker,
            "diff --cc secret/conflict.cs",
            "index 1111111,2222222..3333333",
            "--- a/secret/conflict.cs",
            "+++ b/secret/conflict.cs",
            "@@@ -1,1 -1,1 +1,1 @@@",
            "++" + Marker,
            "");

        var dropped = new ContentExclusions.Tally();
        var kept    = ContentExclusions.WithoutExcludedFiles(diff, _root, dropped);

        Assert.DoesNotContain(Marker, kept);
        Assert.Contains("+visible", kept);
        Assert.Equal(4, dropped.Count);   // renamed, quoted, mnemonic prefixes (diff.mnemonicPrefix), combined (a conflict)
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private bool Git(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false,
                                                     RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>An editor with one active document; records what an edit would have inserted.</summary>
    private sealed class ActiveEditor(ActiveDocument document) : IEditorSurface
    {
        public string? Inserted;
        public bool IsAvailable => true;
        public string? ActiveDocumentPath => document.Path;
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) => Task.FromResult<ActiveDocument?>(document);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) { Inserted = text; return Task.FromResult<string?>("inserted"); }
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) => Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public IReadOnlyList<string> GetOpenDocumentPaths() => [document.Path];
    }
}

public partial class HostServerTests
{
    /// <summary>
    /// VS Code reads its chips and its <c>@path</c>s itself, and sends the code of <c>/explain</c>, <c>/fix</c> and ghost
    /// text: the host it asks refuses an excluded file on each of those requests — and the model is never called.
    /// </summary>
    [Fact]
    public async Task TheEditorsRequests_AboutAnExcludedFile_AreRefused_WithoutTheModel()
    {
        using var h = CreateHarness();
        File.WriteAllText(Path.Combine(h.RootDir, ".aiignore"), "secret.cs\n");
        var secret = Path.Combine(h.RootDir, "secret.cs");
        File.WriteAllText(secret, "class Secret { }\n");
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var modelCalled = false;
        h.Fake.OnChatRequest = (_, _, _, _) => { modelCalled = true; return Task.FromResult(new ChatTurnResult("x", null, 0, 0)); };
        h.Fake.OnFim = (_, _) => { modelCalled = true; return "completed"; };

        var check = await h.Client.InvokeWithParameterObjectAsync<Host.ExclusionsCheckResult>(
                "exclusions/check", new { paths = new[] { secret, Path.Combine(h.RootDir, "open.cs") } })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var excerpt = await h.Client.InvokeWithParameterObjectAsync<Host.CodeExcerptResult>(
                "code/excerpt", new { code = "class Secret { }", fileName = "secret.cs", path = secret })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var action = await h.Client.InvokeWithParameterObjectAsync<Host.CodeActionResultDto>(
                "codeAction/run", new { kind = "fix", text = "class Secret { }", selStart = 0, selEnd = 0, path = secret })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var fim = await h.Client.InvokeWithParameterObjectAsync<string>(
                "fim/complete", new { prefix = "class ", suffix = " { }", path = secret })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Contains(".aiignore", check.Notices[0]);
        Assert.Contains("excluded from AI tools by .aiignore, line 1", check.ForModel[0]);
        Assert.Null(check.Notices[1]);   // WITNESS: a file nothing excludes passes
        Assert.Contains(".aiignore", excerpt.Refusal);
        Assert.Equal(string.Empty, excerpt.Text);
        Assert.Equal("refused", action.Outcome);
        Assert.Contains(".aiignore", action.FailureDetail);
        Assert.Equal(string.Empty, fim);
        Assert.False(modelCalled);

        // Reference arm: the same completion, for a file nothing excludes, reaches the model.
        var open = await h.Client.InvokeWithParameterObjectAsync<string>(
                "fim/complete", new { prefix = "class ", suffix = " { }", path = Path.Combine(h.RootDir, "open.cs") })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Equal("completed", open);
    }
}

/// <summary>
/// The adapters' halves, which no test can run: the Visual Studio window and the VS Code extension read the editor
/// themselves (chips, selections, the active document) — each funnel asks the one reader before the content leaves.
/// </summary>
/// <remarks>Read without comments (a funnel commented out is a funnel removed). The Core halves are run above.</remarks>
public sealed class ContentExclusionAdapterTests
{
    private static string Cs(params string[] path) =>
        ConventionCoverageTests.CodeOnly(Path.Combine([ConventionCoverageTests.RepoRoot(), .. path]));

    [Fact]
    public void VsCode_AsksTheHost_BeforeItSendsAChipATypedPathOrTheActiveCode()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        var turn = WebviewRebuildTests.Body(provider, "private async chatTurn(");
        Assert.Contains("ChatViewProvider.excluded(host, chips.map((a) => a.origin ?? ''))", turn, StringComparison.Ordinal);
        var expand = WebviewRebuildTests.Body(provider,
            "private async expandMentions(prompt: string, host: HostClient): Promise<{ text: string; paths: string[]; labels: string[] }>");
        Assert.Contains("ChatViewProvider.excluded(host, [uri.fsPath])", expand, StringComparison.Ordinal);
        Assert.True(expand.IndexOf("ChatViewProvider.excluded(", StringComparison.Ordinal)
                    < expand.IndexOf("openTextDocument(uri)", StringComparison.Ordinal), "asked BEFORE the file is read");
        const string IsAFile = "isFile = ((await vscode.workspace.fs.stat(uri)).type & vscode.FileType.File) !== 0;";
        Assert.Contains(IsAFile, expand, StringComparison.Ordinal);
        Assert.True(expand.IndexOf(IsAFile, StringComparison.Ordinal)
                    < expand.IndexOf("ChatViewProvider.excluded(", StringComparison.Ordinal),
                    "a token that is no file is never sent to the host");
        Assert.Contains("if (excerpt.refusal)", WebviewRebuildTests.Body(provider, "private async runExplainReview("), StringComparison.Ordinal);
        Assert.Contains("result.outcome === 'refused'", WebviewRebuildTests.Body(provider, "private async runCodeAction("), StringComparison.Ordinal);
        Assert.Contains("path: document.uri.scheme === 'file' ? document.uri.fsPath : undefined",
                        WebviewRebuildTests.TsCode("inlineCompletions.ts"), StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudio_RefusesAnExcludedFile_AtEachOfItsFunnels()
    {
        Assert.Contains("ContentExclusions.NoticeFor(refused.Origin",
            Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"), StringComparison.Ordinal);   // judged again at send
        Assert.Contains("ContentExclusions.NoticeFor(origin ?? sourcePath",
            WebviewRebuildTests.Body(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.Attachments.cs"), "private void AddAttachment("),
            StringComparison.Ordinal);
        Assert.Contains("ContentExclusions.NoticeFor(view.Document.Uri.LocalPath",
            WebviewRebuildTests.Body(Cs("Inferpal", "Commands", "InPlaceCodeEdit.cs"), "public static async Task<InPlaceEditResult> RunAsync("),
            StringComparison.Ordinal);
        Assert.Contains("ContentExclusions.NoticeFor(view.Document.Uri.LocalPath", Cs("Inferpal", "Commands", "InlineEditSelectionCommand.cs"),
            StringComparison.Ordinal);
        Assert.Contains("ContentExclusions.NoticeFor(view.Document.Uri.LocalPath", Cs("Inferpal", "Commands", "SelectionCommandBase.cs"),
            StringComparison.Ordinal);
        Assert.Contains("ContentExclusions.NoticeFor(view.Document.Uri.LocalPath",
            WebviewRebuildTests.Body(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.CodeActions.cs"), "GetActiveCodeAsync(CancellationToken ct)"),
            StringComparison.Ordinal);
        Assert.Contains("MentionController.FileRefusal(filePath", Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.Mentions.cs"),
            StringComparison.Ordinal);
        // Ghost text: the in-process half sends the file, the sidecar (the Core) judges it.
        Assert.Contains("path:        path", Cs("Inferpal.InProc", "GhostText", "GhostTextController.cs"), StringComparison.Ordinal);
        Assert.Contains("ContentExclusions.Of(path) is not null) return string.Empty", Cs("Inferpal.Fim", "Program.cs"),
            StringComparison.Ordinal);
    }
}

public partial class HostServerTests
{
    /// <summary>In a VS Code window of several folders, each folder's ignore files keep ITS files out of reach.</summary>
    [Fact]
    public async Task EachFolderOfTheWindow_KeepsItsOwnFilesOutOfReach()
    {
        using var h = CreateHarness();
        var second = Directory.CreateTempSubdirectory("inferpal-second").FullName;
        try
        {
            File.WriteAllText(Path.Combine(second, ".aiignore"), "private.cs\n");
            File.WriteAllText(Path.Combine(second, "private.cs"), "class P { }\n");
            File.WriteAllText(Path.Combine(second, "public.cs"), "class Q { }\n");
            await h.Client.InvokeWithParameterObjectAsync<Host.InitializeResult>(
                    "initialize", new { rootDir = h.RootDir, otherFolders = new[] { second } })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var check = await h.Client.InvokeWithParameterObjectAsync<Host.ExclusionsCheckResult>(
                    "exclusions/check", new { paths = new[] { Path.Combine(second, "private.cs"), Path.Combine(second, "public.cs") } })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.Contains(".aiignore", check.Notices[0]);
            Assert.Null(check.Notices[1]);   // WITNESS
        }
        finally
        {
            Services.Governance.ContentExclusions.ForgetForTests(second);
            try { Directory.Delete(second, recursive: true); } catch { }
        }
    }
}
