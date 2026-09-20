using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The files the user gives to the system prompt — a pinned file, <c>.inferpal/context.md</c>, a
/// rule — and that the product could not READ.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The system prompt is rebuilt on every change of active file</b> (and on every
/// <c>/clear</c>, on every session load). A failure path that speaks on every pass therefore writes
/// one ring entry per file opened: the ring keeps only <see cref="Diagnostics.Capacity"/> of them,
/// and the fault you came to <c>/diagnostics</c> to find is chased out by the message describing…
/// the other fault.
/// </para>
/// <para>
/// ⚠ This is exactly the class <c>DroppedLineOnce</c> and <c>RecordOnce</c> exist to close — and
/// the rule was written <b>in that very file</b>, three lines above, for the <i>missing</i> pinned
/// file: "this prompt is rebuilt on every change of active file". The <i>unreadable</i> pinned file
/// went through a bare <c>Swallow</c>.
/// </para>
/// <para>
/// And the second half matters as much: the notice must name the <b>consequence</b> — this file is
/// not in the prompt — and not only the exception. The user pinned it so that it would go out with
/// every request.
/// </para>
/// </remarks>
[Collection("Diagnostics")]
public sealed class PromptFileSilenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"prompt-{Guid.NewGuid():N}");
    private readonly string _pinned;
    private readonly string _context;
    private readonly string _rule;
    private FileStream? _holdPinned;
    private FileStream? _holdContext;
    private FileStream? _holdRule;

    public PromptFileSilenceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal", "rules"));
        _pinned  = Path.Combine(_root, "ARCHITECTURE.md");
        _context = Path.Combine(_root, ".inferpal", "context.md");
        _rule    = Path.Combine(_root, ".inferpal", "rules", "style.md");
        File.WriteAllText(_pinned,  "# Architecture\n\nThe file the user pinned.");
        File.WriteAllText(_context, "# Context\n\nThe project context.");
        File.WriteAllText(_rule,    "---\nalwaysApply: true\n---\n\nAlways answer in English.");
    }

    public void Dispose()
    {
        Unlock(ref _holdPinned);
        Unlock(ref _holdContext);
        Unlock(ref _holdRule);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Making a FILE unreadable ──────────────────────────────────────────────

    /// <summary>
    /// The file is held open with <see cref="FileShare.None"/>, as another process would —
    /// <c>File.ReadAllText</c> throws for real. ⚠ The same gesture as <c>MarkdownFolderTests</c>,
    /// and not an ACL nor a <c>chmod</c>: .NET takes a <c>flock</c> under Unix, so the lock holds on
    /// all four CI legs. A witness only checkable under Windows would measure the platform, not the
    /// product — the previous round paid for that.
    /// </summary>
    private static void Lock(string path, ref FileStream? hold) =>
        hold ??= new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

    private static void Unlock(ref FileStream? hold)
    {
        hold?.Dispose();
        hold = null;
    }

    /// <summary>
    /// The witness: without it, this whole file would be green on a machine where the lock does not
    /// take — and would measure nothing at all.
    /// </summary>
    private static void AssertReallyUnreadable(string path)
    {
        Assert.True(File.Exists(path), "the file must exist: that is the other cause, already covered");
        Assert.ThrowsAny<Exception>(() => File.ReadAllText(path));
    }

    private string BuildOnce() =>
        new SystemPromptBuilder(new InferpalConfig { PinnedContextFiles = _pinned })
            .Build("BASE", projectRoot: _root);

    private static string[] Notes() =>
        [.. Diagnostics.Snapshot().Select(e => e.Context + " | " + e.Detail)];

    // ── Once, not on every rebuild ────────────────────────────────────────────

    [Fact]
    public void AnUnreadablePinnedFile_IsReportedOnce_NotOncePerRebuild()
    {
        Lock(_pinned, ref _holdPinned);
        AssertReallyUnreadable(_pinned);
        Diagnostics.Clear();

        for (var i = 0; i < 30; i++) BuildOnce();

        var about = Notes().Where(n => n.Contains("ARCHITECTURE.md")).ToList();
        Assert.Single(about);
    }

    [Fact]
    public void AnUnreadableContextFile_IsReportedOnce_NotOncePerRebuild()
    {
        Lock(_context, ref _holdContext);
        AssertReallyUnreadable(_context);
        Diagnostics.Clear();

        for (var i = 0; i < 30; i++) BuildOnce();

        var about = Notes().Where(n => n.Contains("context.md")).ToList();
        Assert.Single(about);
    }

    /// <summary>
    /// The rules are re-read by the same rebuild — and the ring took one entry per unreadable rule
    /// AND per pass. ⚠ The second arm matters as much: the list returned to the caller (the one
    /// <c>/rules</c> shows) must stay COMPLETE on every call. Deduplicating the notice must not
    /// deduplicate the report.
    /// </summary>
    [Fact]
    public void AnUnreadableRuleFile_IsReportedOnce_ButListedEveryTime()
    {
        Lock(_rule, ref _holdRule);
        AssertReallyUnreadable(_rule);
        Diagnostics.Clear();

        for (var i = 0; i < 30; i++) BuildOnce();

        Assert.Single(Notes().Where(n => n.Contains("style.md")));

        var rulesDir = Path.Combine(_root, ".inferpal", "rules");
        for (var i = 0; i < 3; i++)
        {
            Inferpal.Services.Governance.RulesService.Load(rulesDir, out var unreadable);
            Assert.Contains("style.md", unreadable);
        }
    }

    // ── And the notice says the CONSEQUENCE ──────────────────────────────────

    /// <summary>
    /// ⚠ A positive assertion on the text: a notice carrying only the exception message tells the
    /// user that access was denied, not that <b>their</b> file has left the system prompt — the one
    /// fact that makes them change anything.
    /// </summary>
    [Fact]
    public void TheNote_NamesTheConsequence_NotJustTheException()
    {
        Lock(_pinned, ref _holdPinned);
        AssertReallyUnreadable(_pinned);
        Diagnostics.Clear();

        var prompt = BuildOnce();

        Assert.DoesNotContain("Architecture", prompt);   // the file really is not in there
        var note = Assert.Single(Notes().Where(n => n.Contains("ARCHITECTURE.md")));
        Assert.Contains("system prompt", note, StringComparison.OrdinalIgnoreCase);
    }

    // ── Reference arms ───────────────────────────────────────────────────────

    /// <summary>A readable file says nothing at all, and it really is in the prompt.</summary>
    [Fact]
    public void AReadableFile_SaysNothing_AndIsInThePrompt()
    {
        Diagnostics.Clear();

        var prompt = BuildOnce();

        Assert.Contains("The file the user pinned.", prompt);
        Assert.Contains("The project context.", prompt);
        Assert.Empty(Notes().Where(n => n.Contains("ARCHITECTURE.md") || n.Contains("context.md")));
    }

    /// <summary>
    /// ⚠ "Once" must not mean "once in the life of the process": a file repaired then broken again
    /// must say so again, like the missing pinned file that comes back.
    /// </summary>
    [Fact]
    public void OnceItIsReadableAgain_ALaterFailureSpeaksAgain()
    {
        Lock(_pinned, ref _holdPinned);
        AssertReallyUnreadable(_pinned);
        Diagnostics.Clear();
        BuildOnce();

        Unlock(ref _holdPinned);
        BuildOnce();                                   // the read goes through: the memory forgets
        Lock(_pinned, ref _holdPinned);
        AssertReallyUnreadable(_pinned);
        BuildOnce();

        Assert.Equal(2, Notes().Count(n => n.Contains("ARCHITECTURE.md")));
    }
}
