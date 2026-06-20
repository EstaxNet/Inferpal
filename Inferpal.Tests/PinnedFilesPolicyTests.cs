using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

public class PinnedFilesPolicyTests
{
    // ── ParseActive ─────────────────────────────────────────────────────────────

    [Fact]
    public void ParseActive_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(PinnedFilesPolicy.ParseActive(null));
        Assert.Empty(PinnedFilesPolicy.ParseActive(string.Empty));
    }

    [Fact]
    public void ParseActive_SkipsDisabledEntries()
    {
        var active = PinnedFilesPolicy.ParseActive("C:\\a.cs\n#C:\\b.cs\nC:\\c.cs");

        Assert.Equal(["C:\\a.cs", "C:\\c.cs"], active);
    }

    [Fact]
    public void ParseActive_TrimsAndDropsBlankLines()
    {
        var active = PinnedFilesPolicy.ParseActive("  C:\\a.cs  \n\n   \nC:\\b.cs");

        Assert.Equal(["C:\\a.cs", "C:\\b.cs"], active);
    }

    [Fact]
    public void ParseActive_CapsAtMaxPinned()
    {
        var active = PinnedFilesPolicy.ParseActive("a\nb\nc\nd\ne");

        Assert.Equal(PinnedFilesPolicy.MaxPinned, active.Count);
        Assert.Equal(["a", "b", "c"], active);
    }

    [Fact]
    public void ParseActive_DisabledEntriesDoNotConsumeTheCap()
    {
        var active = PinnedFilesPolicy.ParseActive("#x\n#y\na\nb\nc");

        Assert.Equal(["a", "b", "c"], active);
    }

    // ── Decide ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Decide_EmptyPath_IsInvalid()
    {
        Assert.Equal(PinDecision.Invalid, PinnedFilesPolicy.Decide([], ""));
    }

    [Fact]
    public void Decide_NewPath_IsPin()
    {
        Assert.Equal(PinDecision.Pin, PinnedFilesPolicy.Decide(["C:\\a.cs"], "C:\\b.cs"));
    }

    [Fact]
    public void Decide_DuplicateIsCaseInsensitive()
    {
        Assert.Equal(PinDecision.Duplicate,
            PinnedFilesPolicy.Decide(["C:\\Foo\\Bar.cs"], "c:\\foo\\bar.cs"));
    }

    [Fact]
    public void Decide_AtCap_IsCapReached()
    {
        Assert.Equal(PinDecision.CapReached,
            PinnedFilesPolicy.Decide(["a", "b", "c"], "d"));
    }

    [Fact]
    public void Decide_DuplicateWinsOverCap()
    {
        // Re-pinning an already-pinned file at the cap is a silent no-op, not a cap warning.
        Assert.Equal(PinDecision.Duplicate,
            PinnedFilesPolicy.Decide(["a", "b", "c"], "b"));
    }

    // ── Serialize ───────────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_JoinsActivePathsWithNewlines()
    {
        Assert.Equal("a\nb", PinnedFilesPolicy.Serialize(["a", "b"], previousConfig: null));
    }

    [Fact]
    public void Serialize_PreservesDisabledEntriesFromPreviousConfig()
    {
        var result = PinnedFilesPolicy.Serialize(["a"], "old1\n#disabled1\nold2\n#disabled2");

        Assert.Equal("a\n#disabled1\n#disabled2", result);
    }

    [Fact]
    public void Serialize_RoundTripsWithParseActive()
    {
        const string config = "C:\\a.cs\n#C:\\off.cs\nC:\\b.cs";

        var active = PinnedFilesPolicy.ParseActive(config);
        var saved  = PinnedFilesPolicy.Serialize(active, config);

        Assert.Equal("C:\\a.cs\nC:\\b.cs\n#C:\\off.cs", saved);     // disabled entry survives, moved last
        Assert.Equal(active, PinnedFilesPolicy.ParseActive(saved)); // active set unchanged
    }

    [Fact]
    public void Serialize_NoActivePins_KeepsOnlyDisabled()
    {
        Assert.Equal("#x", PinnedFilesPolicy.Serialize([], "a\n#x"));
    }
}
