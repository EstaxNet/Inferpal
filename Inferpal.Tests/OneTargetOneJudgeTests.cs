using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Governance;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ What is judged is what runs: every approval control — the ignore files, the workspace's confinement, the forced card
/// of the agent's own instructions, a <c>deny</c> rule — judges the FILE a call reaches, never only the spelling the model
/// wrote. A link, a hard link, a device prefix (<c>\\?\</c>), a local administrative share or an NTFS index suffix each
/// reaches the file while its text names another path.
/// </summary>
public sealed class OneTargetOneJudgeTests : IDisposable
{
    // ⚠ A rooted index watches the disk (one inotify instance per watcher on Linux, 128 per user): never left undisposed.
    private readonly List<IDisposable> _owned = [];
    private const string Marker = "EXCLUDED_CONTENT_58";
    private readonly string _base = Path.Combine(Path.GetTempPath(), "inferpal-tests", "one-judge-" + Guid.NewGuid().ToString("N")[..8]);
    private string W => Path.Combine(_base, "ws");

    public OneTargetOneJudgeTests()
    {
        Directory.CreateDirectory(Path.Combine(W, "secrets"));
        Directory.CreateDirectory(Path.Combine(W, ".inferpal"));
        Directory.CreateDirectory(Path.Combine(W, "docs"));
        File.WriteAllText(Path.Combine(W, ".aiignore"), "secret.txt\nsecrets/\n");
        File.WriteAllText(Path.Combine(W, "secret.txt"), Marker + "\n");
        File.WriteAllText(Path.Combine(W, "secrets", "key.pem"), Marker + "_KEY\n");
        File.WriteAllText(Path.Combine(W, ".inferpal", "memory.md"), "v0\n");
        File.WriteAllText(Path.Combine(W, "notes.md"), "v0\n");
        File.WriteAllText(Path.Combine(W, "docs", "readme.md"), "docs\n");
        File.WriteAllText(Path.Combine(_base, "outside.txt"), "outside v0\n");
        ContentExclusions.Declare(W, () => ContentExclusions.DefaultFiles);
    }

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        try { Directory.Delete(_base, recursive: true); } catch { /* cleanup */ }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool Link(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
            p.WaitForExit(30_000);
            return Directory.Exists(link);
        }
        catch (Exception) { return false; }
    }

    /// <summary>A hard link named <paramref name="name"/> in the workspace to <paramref name="file"/> (Windows, no admin).</summary>
    private string? HardLink(string name, string file)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c mklink /H \"{Path.Combine(W, name)}\" \"{file}\"")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        p.WaitForExit(30_000);
        return File.Exists(Path.Combine(W, name)) ? name : null;
    }

    private sealed class Always(InferpalConfig config, Func<string?> root) : ApprovalServiceBase(config, root)
    {
        public int Cards;
        protected override Task<ApprovalDecision> PromptUserAsync(string message, Services.CodeActions.DiffInfo? diff, CancellationToken ct)
        {
            Cards++;
            return Task.FromResult(ApprovalDecision.Always);
        }
    }

    private sealed class Editor(string path, string target) : IEditorSurface
    {
        public string Path = path, Target = target;
        public bool IsAvailable => true;
        public string? ActiveDocumentPath => Path;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [Path];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<ActiveDocument?>(new ActiveDocument(Path, File.ReadAllText(Target)));
        public Task<string?> InsertAtCursorAsync(string text, string p, CancellationToken ct)
        {
            File.AppendAllText(Target, text);
            return Task.FromResult<string?>(p);
        }
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, string p, string? selection, CancellationToken ct) =>
            Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private (ToolRegistry Tools, Always Approval) Registry(InferpalConfig? config = null, IEditorSurface? editor = null)
    {
        config ??= new InferpalConfig();
        var client = new FakeInferenceProvider();
        editor ??= new NullEditorSurface();
        var approval = new Always(config, () => W);
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _owned.Add(index);
        index.SetRoot(W);
        return (new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                 new McpToolService(config, approval), new DocsIndexService(client, config),
                                 new OpenDocumentOverlay(), new NullDebugSession()), approval);
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    /// <summary>"Always" given to <paramref name="tool"/> on a plain file, and WITNESSED: a second plain call asks nothing.</summary>
    private static async Task GrantAsync(ToolRegistry tools, Always approval, string tool, string root)
    {
        var plain = Path.Combine(root, "plain-" + tool + ".txt");
        File.WriteAllText(plain, "plain\n");
        object args = tool == "delete_file" ? new { path = plain } : new { path = plain, content = "plain 2\n" };
        await tools.ExecuteAsync(tool, Args(args), default);
        File.WriteAllText(plain, "plain\n");
        var granted = approval.Cards;
        await tools.ExecuteAsync(tool, Args(args), default);
        Assert.Equal(granted, approval.Cards);   // WITNESS: the grant holds
    }

    // ── Exclusions ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnExcludedFile_ReadThroughAHardLink_StaysOut()
    {
        if (HardLink("copy.txt", Path.Combine(W, "secret.txt")) is not { } link) return;
        var (tools, _) = Registry();

        var plain = await tools.ExecuteAsync("read_file", Args(new { path = "secret.txt" }), default);
        var linked = await tools.ExecuteAsync("read_file", Args(new { path = link }), default);

        Assert.DoesNotContain(Marker, plain, StringComparison.Ordinal);    // reference arm: the name itself is held
        Assert.DoesNotContain(Marker, linked, StringComparison.Ordinal);
        Assert.Contains(".aiignore", linked, StringComparison.Ordinal);   // and said, by the rule that holds it
    }

    [Theory]
    [InlineData(@"\\?\")]
    [InlineData(@"\\.\")]
    [InlineData("UNC")]
    [InlineData("device UNC")]
    public void AnExcludedFile_NamedWithADevicePrefixOrTheLocalShare_IsExcluded(string form)
    {
        if (!OperatingSystem.IsWindows()) return;
        var full = Path.Combine(W, "secret.txt");
        var share = @"localhost\" + full[0] + "$" + full[2..];
        var spelling = form switch { "UNC" => @"\\" + share, "device UNC" => @"\\?\UNC\" + share, _ => form + full };
        Assert.True(File.Exists(spelling));   // WITNESS: the spelling reaches the file

        // The funnel every tool — MCP included — passes: the argument names the excluded file.
        Assert.NotNull(ToolRegistry.ExcludedNamedFile(Args(new { file = full }), W));       // reference arm
        Assert.NotNull(ToolRegistry.ExcludedNamedFile(Args(new { file = spelling }), W));
    }

    /// <summary>A folder the ignore files exclude, named as it is, is judged by the walk that lists it (each file left out
    /// and counted) — never refused whole at the funnel; named through a link that leads into it, it is excluded.</summary>
    [Fact]
    public void AnExcludedFolder_IsRefusedAtTheFunnel_OnlyWhenItsNameLeadsElsewhere()
    {
        Assert.Null(ToolRegistry.ExcludedNamedFile(Args(new { path = "secrets" }), W));
        Assert.NotNull(ToolRegistry.ExcludedNamedFile(Args(new { path = "secrets/key.pem" }), W));   // WITNESS: excluded
        if (!Link(Path.Combine(W, "lnk"), Path.Combine(W, "secrets"))) return;
        Assert.NotNull(ToolRegistry.ExcludedNamedFile(Args(new { path = "lnk" }), W));
    }

    [Fact]
    public async Task AnExcludedFile_OpenedThroughALink_IsNotWrittenAtTheCaret()
    {
        if (!Link(Path.Combine(W, "lnk"), Path.Combine(W, "secrets"))) return;
        var key = Path.Combine(W, "secrets", "key.pem");
        var editor = new Editor(Path.Combine(W, "lnk", "key.pem"), key);
        var (tools, _) = Registry(editor: editor);

        var result = await tools.ExecuteAsync("insert_at_cursor", Args(new { text = "injected\n" }), default);

        Assert.Equal(Marker + "_KEY\n", File.ReadAllText(key));
        Assert.Contains(".aiignore", result, StringComparison.Ordinal);
    }

    // ── Confinement ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFileOutsideTheWorkspace_WrittenThroughAHardLinkInside_IsRefused()
    {
        var outside = Path.Combine(_base, "outside.txt");
        if (HardLink("inside.txt", outside) is not { } link) return;
        var (tools, _) = Registry();

        var result = await tools.ExecuteAsync("write_file", Args(new { path = link, content = "escaped\n" }), default);

        Assert.Equal("outside v0\n", File.ReadAllText(outside));
        Assert.Contains("outside the workspace", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An open document written through its buffer is saved to the same file: the same refusal as a write to disk.</summary>
    [Fact]
    public async Task AnOpenDocument_ThatIsAlsoOutsideTheWorkspace_IsRefused_LikeAWriteToDisk()
    {
        var outside = Path.Combine(_base, "outside.txt");
        if (HardLink("inside.txt", outside) is not { } link) return;
        var path = Path.Combine(W, link);
        var (tools, approval) = Registry(editor: new OpenDocument(path, File.ReadAllText(path)));

        var result = await tools.ExecuteAsync("insert_at_cursor", Args(new { text = "escaped" }), default);

        Assert.Equal("outside v0\n", File.ReadAllText(outside));
        Assert.Contains("outside the workspace", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(ToolFailure.IsFailure(result), result);
        Assert.Equal(0, approval.Cards);   // before the card: answered, the write would be refused all the same
    }

    private sealed class OpenDocument(string path, string text) : IEditorSurface
    {
        public bool IsAvailable => true;
        public string? ActiveDocumentPath => path;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [path];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) => Task.FromResult<ActiveDocument?>(new ActiveDocument(path, text));
        public Task<string?> InsertAtCursorAsync(string written, string at, CancellationToken ct) => Task.FromResult<string?>(path);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string written, string at, string? selection, CancellationToken ct) =>
            Task.FromResult<EditorEditResult?>(new EditorEditResult(path, ReplacedSelection: true));
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>A file that is also, by a hard link, outside the workspace is READ as the project has it — a dependency
    /// manager links its packages to a shared store (pnpm's node_modules, uv's environments) —; only a write is refused.</summary>
    [Fact]
    public async Task AFileHardLinkedToAStoreOutside_IsRead_ButNotWritten()
    {
        var store = Path.Combine(_base, "store.js");
        File.WriteAllText(store, "module.exports = 42;\n");
        if (HardLink("dep.js", store) is not { } link) return;
        var (tools, _) = Registry();

        var read = await tools.ExecuteAsync("read_file", Args(new { path = link }), default);
        await tools.ExecuteAsync("write_file", Args(new { path = link, content = "changed\n" }), default);

        Assert.Contains("module.exports = 42", read, StringComparison.Ordinal);
        Assert.Equal("module.exports = 42;\n", File.ReadAllText(store));
    }

    /// <summary>⚠ <c>\\?\</c> leaves its <c>..</c> unresolved as text: <c>\\?\C:\ws\..\outside</c> is outside.</summary>
    [Fact]
    public void ADevicePath_ThatClimbsOut_IsOutside()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<ArgumentException>(() => PathSanitizer.AssertUnderRoot(@"\\?\" + Path.Combine(W, "..", "outside.txt"), W));
        PathSanitizer.AssertUnderRoot(@"\\?\" + Path.Combine(W, "notes.md"), W);   // reference arm: inside is inside
    }

    // ── The forced card of the agent's own instructions ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSystemPrompt_WrittenThroughALinkedFolder_AsksEvenUnderAlways()
    {
        if (!Link(Path.Combine(W, "inflnk"), Path.Combine(W, ".inferpal"))) return;
        var memory = Path.Combine(W, ".inferpal", "memory.md");
        var (tools, approval) = Registry();
        await GrantAsync(tools, approval, "write_file", W);
        await GrantAsync(tools, approval, "delete_file", W);

        foreach (var tool in new[] { "write_file", "delete_file" })
        {
            File.WriteAllText(memory, "v0\n");
            var before = approval.Cards;
            object args = tool == "delete_file" ? new { path = "inflnk/memory.md" } : new { path = "inflnk/memory.md", content = "injected\n" };
            await tools.ExecuteAsync(tool, Args(args), default);
            Assert.True(approval.Cards > before, $"{tool} wrote the system prompt through a link without a card");
        }
    }

    [Fact]
    public async Task TheSystemPrompt_WrittenThroughAHardLink_AsksEvenUnderAlways()
    {
        var memory = Path.Combine(W, ".inferpal", "memory.md");
        if (HardLink("mem.md", memory) is not { } link) return;
        var (tools, approval) = Registry();
        await GrantAsync(tools, approval, "write_file", W);

        var before = approval.Cards;
        await tools.ExecuteAsync("write_file", Args(new { path = link, content = "injected\n" }), default);

        Assert.True(approval.Cards > before);
    }

    [Theory]
    [InlineData("link")]
    [InlineData("index")]
    public async Task TheSystemPrompt_InsertedAtTheCaret_AsksWhateverSpellingTheEditorGives(string form)
    {
        var memory = Path.Combine(W, ".inferpal", "memory.md");
        string spelling;
        if (form == "link")
        {
            if (!Link(Path.Combine(W, "inflnk"), Path.Combine(W, ".inferpal"))) return;
            spelling = Path.Combine(W, "inflnk", "memory.md");
        }
        else
        {
            if (!OperatingSystem.IsWindows()) return;
            spelling = Path.Combine(W, ".inferpal::$INDEX_ALLOCATION", "memory.md");
        }
        Assert.True(File.Exists(spelling));   // WITNESS: the spelling reaches the file
        var readme = Path.Combine(W, "docs", "readme.md");
        var editor = new Editor(readme, readme);
        var (tools, approval) = Registry(editor: editor);
        await tools.ExecuteAsync("insert_at_cursor", Args(new { text = "x\n" }), default);   // the tool's own "Always"
        var granted = approval.Cards;
        await tools.ExecuteAsync("insert_at_cursor", Args(new { text = "y\n" }), default);
        Assert.Equal(granted, approval.Cards);   // WITNESS: the grant holds

        editor.Path = spelling;
        editor.Target = memory;
        await tools.ExecuteAsync("insert_at_cursor", Args(new { text = "injected\n" }), default);

        Assert.True(approval.Cards > granted);
    }

    [Theory]
    [InlineData("link")]
    [InlineData("index")]
    [InlineData("index, a file it creates")]
    [InlineData("link, after content full of commas")]
    public void AnMcpToolsArguments_NamingTheSystemPrompt_ForceTheCard(string form)
    {
        string spelling;
        if (form.StartsWith("link", StringComparison.Ordinal))
        {
            if (!Link(Path.Combine(W, "inflnk"), Path.Combine(W, ".inferpal"))) return;
            spelling = Path.Combine(W, "inflnk", "memory.md");
        }
        else
        {
            if (!OperatingSystem.IsWindows()) return;
            // A file the call creates has no name the system could list yet: only the suffix taken off says where it lands.
            spelling = form == "index"
                ? Path.Combine(W, ".inferpal::$INDEX_ALLOCATION", "memory.md")
                : Path.Combine(W, ".inferpal::$INDEX_ALLOCATION", "rules", "new.md");
        }
        // Code as content: its commas split the raw JSON line into many runs, none of which may spend the path's turn.
        var content = form.Contains("commas", StringComparison.Ordinal) ? string.Join(", ", Enumerable.Range(0, 40).Select(i => $"f({i}, {i})")) : "x";
        var subject = McpApprovalSubject.From(Args(new { text = content, file = spelling }));

        Assert.True(AgentInstructionFiles.Targets(McpApprovalSubject.From(Args(new { file = Path.Combine(W, ".inferpal", "memory.md") }))));   // reference arm
        Assert.True(AgentInstructionFiles.Targets(subject));
    }

    // ── Rules ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADenyRule_OnAFile_HoldsThroughAHardLink()
    {
        var notes = Path.Combine(W, "notes.md");
        if (HardLink("n2.md", notes) is not { } link) return;
        var (tools, _) = Registry(new InferpalConfig { PermissionRules = "deny write_file notes\\.md$" });

        var plain = await tools.ExecuteAsync("write_file", Args(new { path = "notes.md", content = "changed\n" }), default);
        Assert.Equal("v0\n", File.ReadAllText(notes));   // reference arm: the name itself is refused
        var linked = await tools.ExecuteAsync("write_file", Args(new { path = link, content = "changed\n" }), default);

        Assert.Equal("v0\n", File.ReadAllText(notes));
        Assert.Contains("notes", linked, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠ An allow rule stays the spelling's: in a workspace opened through a link (macOS's <c>/var</c>, a junction to
    /// another drive), the rule the card proposes — anchored on the folder as spelled — still answers. A deny holds on
    /// every name (<see cref="ADenyRule_OnAFile_HoldsThroughAHardLink"/>).
    /// </summary>
    [Fact]
    public async Task AnAllowRule_InAWorkspaceOpenedThroughALink_StillAnswers()
    {
        var linked = Path.Combine(_base, "linked-ws");
        if (!Link(linked, W)) return;
        var rule = "allow write_file ^" + System.Text.RegularExpressions.Regex.Escape(Path.Combine(linked, "docs")) + "[\\\\/]";
        var config = new InferpalConfig { PermissionRules = rule };
        var client = new FakeInferenceProvider();
        var approval = new Always(config, () => linked);
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _owned.Add(index);
        index.SetRoot(linked);
        var editor = new NullEditorSurface();
        var tools = new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                     new McpToolService(config, approval), new DocsIndexService(client, config),
                                     new OpenDocumentOverlay(), new NullDebugSession());

        var result = await tools.ExecuteAsync("write_file", Args(new { path = "docs/readme.md", content = "docs 2\n" }), default);

        Assert.Equal("docs 2\n", File.ReadAllText(Path.Combine(W, "docs", "readme.md")));   // WITNESS: written
        Assert.Equal(0, approval.Cards);
    }

    // ── What a write makes run later, and where a command runs ───────────────────────────────────────────────────

    private static void Git(string dir, string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", args)
        { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit(30_000);
    }

    /// <summary>
    /// ⚠ The repository's git folder holds settings that RUN programs — <c>core.fsmonitor</c> at the next git status, a
    /// hook, a filter driver —: a write there is asked about even under an "Always" given to the writing tool.
    /// </summary>
    [Fact]
    public async Task AWriteIntoTheGitFolder_AsksEvenUnderAlways()
    {
        Git(W, "init -q");
        var config = Path.Combine(W, ".git", "config");
        Assert.True(File.Exists(config));   // WITNESS: a repository
        var (tools, approval) = Registry();
        await GrantAsync(tools, approval, "write_file", W);

        var before = approval.Cards;
        await tools.ExecuteAsync("write_file", Args(new { path = ".git/config", content = File.ReadAllText(config) + "# x\n" }), default);

        Assert.True(approval.Cards > before);
    }

    /// <summary>
    /// ⚠ Inferpal's own git readings never run the repository's <c>core.fsmonitor</c>: set in <c>.git/config</c> — by a
    /// clone's tooling, or a write asked about once —, the program ran at every status the funnel takes around a call.
    /// </summary>
    [Fact]
    public async Task InferpalsOwnGitReadings_DoNotRunTheRepositorysFsmonitor()
    {
        Git(W, "init -q");
        var marker = Path.Combine(_base, "FSMONITOR_RAN");
        var program = OperatingSystem.IsWindows()
            ? "cmd /c type nul > " + marker.Replace('\\', '/') + " & rem"
            : "touch '" + marker + "'; true";
        File.AppendAllText(Path.Combine(W, ".git", "config"), "[core]\n\tfsmonitor = \"" + program.Replace("\"", "\\\"") + "\"\n");
        Git(W, "status");   // WITNESS: git itself runs it
        if (!File.Exists(marker)) return;   // this git does not honour core.fsmonitor: nothing to measure
        File.Delete(marker);

        var status = await Services.GitProcess.CaptureAsync("status --porcelain", W, default);
        Assert.Equal(0, status.ExitCode);
        Assert.False(File.Exists(marker));

        // The launcher of the status taken around every watched call.
        await Services.GitProcess.CaptureBytesAsync(["status", "--porcelain=v1", "-z"], W, default, maxBytes: 1 << 20);
        Assert.False(File.Exists(marker));
    }

    /// <summary>⚠ Inferpal's own diffs never run the repository's <c>diff.&lt;driver&gt;.textconv</c> program.</summary>
    [Fact]
    public async Task InferpalsOwnDiffs_DoNotRunTheRepositorysTextconv()
    {
        Git(W, "init -q");
        Git(W, "-c user.email=t@t -c user.name=t add docs/readme.md");
        Git(W, "-c user.email=t@t -c user.name=t commit -q -m x");
        var marker = Path.Combine(_base, "TEXTCONV_RAN");
        var program = OperatingSystem.IsWindows()
            ? "cmd /c type nul > " + marker.Replace('\\', '/') + " & type"
            : "sh -c 'touch \"" + marker + "\"; cat \"$1\"' --";
        File.AppendAllText(Path.Combine(W, ".git", "config"), "[diff \"x\"]\n\ttextconv = \"" + program.Replace("\"", "\\\"") + "\"\n");
        File.WriteAllText(Path.Combine(W, ".gitattributes"), "*.md diff=x\n");
        File.WriteAllText(Path.Combine(W, "docs", "readme.md"), "docs changed\n");
        Git(W, "diff");   // WITNESS: git itself runs it
        if (!File.Exists(marker)) return;   // this git does not run the driver here: nothing to measure
        File.Delete(marker);

        await Services.GitProcess.CaptureAsync("diff", W, default);

        Assert.False(File.Exists(marker));
    }

    /// <summary>
    /// ⚠ A rule judges a command line, never the folder it runs in: <c>allow run_command ^git status</c> let the command run
    /// outside the workspace — named by <c>working_directory</c>, or where an earlier <c>cd</c> left the session — without a
    /// card. Outside the workspace the card is asked; inside, the rule still answers.
    /// </summary>
    [Fact]
    public async Task ACommandRunOutsideTheWorkspace_IsAskedAbout_EvenUnderAnAllowRule()
    {
        var (tools, approval) = Registry(new InferpalConfig { PermissionRules = "allow run_command ^git status" });

        var before = approval.Cards;
        await tools.ExecuteAsync("run_command", Args(new { command = "git status", working_directory = "docs" }), default);
        Assert.Equal(before, approval.Cards);   // reference arm: inside, the rule answers

        await tools.ExecuteAsync("run_command", Args(new { command = "git status", working_directory = ".." }), default);
        Assert.True(approval.Cards > before);
    }
    // ── The corpus: every spelling of one file, every control ─────────────────────────────────────────────────────

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, System.Text.StringBuilder shortPath, uint size);

    private static string? ShortName(string full)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(full)) return null;
        var sb = new System.Text.StringBuilder(1024);
        var n = GetShortPathNameW(full, sb, (uint)sb.Capacity);
        return n == 0 || string.Equals(sb.ToString(), full, StringComparison.OrdinalIgnoreCase) ? null : sb.ToString();
    }

    /// <summary>Every spelling of <paramref name="rel"/> the corpus names that this system can build.</summary>
    private IEnumerable<(string Label, string Spelling)> Spellings(string rel, string? linked)
    {
        var abs = Path.Combine(W, rel.Replace('/', Path.DirectorySeparatorChar));
        (string, string?)[] all =
        [
            ("as written", rel), ("./", "./" + rel), ("y/..", "docs/../" + rel), ("a/b/../..", "docs/x/../../" + rel),
            ("absolute", abs), ("absolute, forward slashes", abs.Replace('\\', '/')), ("ab://..", "ab://../" + rel),
            ("double separator", W + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar + rel.Replace('/', Path.DirectorySeparatorChar)),
            ("linked folder", linked), ("hard link", HardLink("hl-" + Guid.NewGuid().ToString("N")[..6] + ".bin", abs)),
        ];
        if (OperatingSystem.IsWindows())
            all = [.. all, ("UPPER", rel.ToUpperInvariant()), ("::$DATA", rel + "::$DATA"), ("trailing dot", rel + "."),
                   ("trailing space", rel + " "), ("8.3", ShortName(abs)), (@"\\?\", @"\\?\" + abs), (@"\\.\", @"\\.\" + abs),
                   ("local share", @"\\localhost\" + abs[0] + "$" + abs[2..]), ("drive in lower case", char.ToLowerInvariant(abs[0]) + abs[1..])];
        foreach (var (label, spelling) in all)
            if (spelling is not null) yield return (label, spelling);
    }

    /// <summary>
    /// Whether <paramref name="spelling"/>, read from the workspace as a tool reads it, opens <paramref name="file"/> itself:
    /// under Windows the file held without sharing refuses a second opening by any of its names; elsewhere, by its existence.
    /// </summary>
    private bool Reaches(string spelling, string file)
    {
        string full;
        try { full = Path.IsPathFullyQualified(spelling) ? spelling : Path.GetFullPath(spelling, W); }
        catch (Exception) { return false; }
        if (!OperatingSystem.IsWindows()) return File.Exists(full);
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using var other = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException) { return false; }
        catch (IOException) { return true; }   // a sharing violation: the same file
    }

    /// <summary>
    /// ⚠ The property itself, on the whole corpus: every spelling that reaches a file gets the answer its own name gets — an
    /// excluded file is never read nor written, a file outside the workspace never changed, the agent's instructions never
    /// written without a card, a file a deny rule names never changed. Links lead into real folders, so that a name alone
    /// never answers for the resolution.
    /// </summary>
    [Fact]
    public async Task EverySpelling_OfOneFile_GetsTheAnswerOfItsName()
    {
        Directory.CreateDirectory(Path.Combine(W, "docs", "x"));
        File.WriteAllText(Path.Combine(W, "AGENTS.md"), "v0\n");
        var notes = Path.Combine(W, "docs", "notes.md");
        File.WriteAllText(notes, "v0\n");
        string? Linked(string link, string target, string file) => Link(Path.Combine(W, link), target) ? link + "/" + file : null;
        var keyLink = Linked("lnk-secrets", Path.Combine(W, "secrets"), "key.pem");
        var memoryLink = Linked("lnk-inferpal", Path.Combine(W, ".inferpal"), "memory.md");
        var notesLink = Linked("lnk-docs", Path.Combine(W, "docs"), "notes.md");
        var outsideLink = Linked("lnk-out", _base, "outside.txt");
        var (tools, approval) = Registry(new InferpalConfig { PermissionRules = "deny write_file [\\\\/]docs[\\\\/]notes\\.md$" });
        await GrantAsync(tools, approval, "write_file", W);
        var divergent = new List<string>();
        var played = 0;

        var key = Path.Combine(W, "secrets", "key.pem");
        foreach (var (label, spelling) in Spellings("secrets/key.pem", keyLink).Where(s => Reaches(s.Spelling, key)))
        {
            played++;
            var read = await tools.ExecuteAsync("read_file", Args(new { path = spelling }), default);
            if (read.Contains(Marker, StringComparison.Ordinal)) divergent.Add($"read excluded {label}: {spelling}");
            await tools.ExecuteAsync("write_file", Args(new { path = spelling, content = "overwritten\n" }), default);
            if (File.ReadAllText(key) != Marker + "_KEY\n") divergent.Add($"write excluded {label}: {spelling}");
            File.WriteAllText(key, Marker + "_KEY\n");
        }
        var outside = Path.Combine(_base, "outside.txt");
        foreach (var (label, spelling) in Spellings("../outside.txt", outsideLink).Where(s => Reaches(s.Spelling, outside)))
        {
            played++;
            await tools.ExecuteAsync("write_file", Args(new { path = spelling, content = "escaped\n" }), default);
            if (File.ReadAllText(outside) != "outside v0\n") divergent.Add($"confinement {label}: {spelling}");
            File.WriteAllText(outside, "outside v0\n");
        }
        foreach (var (target, link) in new[] { (".inferpal/memory.md", memoryLink), ("AGENTS.md", null) })
        {
            var file = Path.Combine(W, target.Replace('/', Path.DirectorySeparatorChar));
            foreach (var (label, spelling) in Spellings(target, link).Where(s => Reaches(s.Spelling, file)))
            {
                played++;
                var before = approval.Cards;
                await tools.ExecuteAsync("write_file", Args(new { path = spelling, content = "v0\ninjected\n" }), default);
                if (File.ReadAllText(file) != "v0\n" && approval.Cards == before) divergent.Add($"forced card {target} {label}: {spelling}");
                File.WriteAllText(file, "v0\n");
            }
        }
        foreach (var (label, spelling) in Spellings("docs/notes.md", notesLink).Where(s => Reaches(s.Spelling, notes)))
        {
            played++;
            await tools.ExecuteAsync("write_file", Args(new { path = spelling, content = "changed\n" }), default);
            if (File.ReadAllText(notes) != "v0\n") divergent.Add($"deny {label}: {spelling}");
            File.WriteAllText(notes, "v0\n");
        }

        Assert.True(played >= (OperatingSystem.IsWindows() ? 40 : 15), $"witness: {played} spellings reached their file");
        Assert.Empty(divergent);
    }
}
