using System.IO;
using Xunit;

namespace Inferpal.Tests;

// Persistent plans (roadmap §17): the file format, the one edit the product may make to it, and the
// containment of a name that can come from the model.
public class PlanDocumentTests
{
    // ── Parsing ────────────────────────────────────────────────────────────────

    [Fact]
    public void StepsAreNumberedByPosition_NotByWhatTheAuthorTyped()
    {
        // The author's own "1." is stripped: showing both numbers lets them disagree the moment a
        // step is inserted by hand, and the file is meant to be edited by hand.
        var doc = PlanDocument.Parse("""
            # Port the index

            - [x] 1. Read the C# index
            - [ ] 7. Add a TS branch
            """);

        Assert.Equal([1, 2], doc.Steps.Select(s => s.Number));
        Assert.Equal(["Read the C# index", "Add a TS branch"], doc.Steps.Select(s => s.Text));
        Assert.Equal(1, doc.DoneCount);
        Assert.Equal("Add a TS branch", doc.NextStep?.Text);
    }

    [Theory]
    [InlineData("- [ ] step")]
    [InlineData("* [ ] step")]
    [InlineData("+ [ ] step")]
    [InlineData("1. [ ] step")]
    [InlineData("2) [ ] step")]
    [InlineData("   - [ ] step")]
    public void EveryCheckboxShapeAnEditorProduces_IsRecognised(string line)
    {
        var doc = PlanDocument.Parse("# T\n\n" + line);

        Assert.Equal("step", Assert.Single(doc.Steps).Text);
    }

    [Fact]
    public void TheTitleFallsBackFromHeadingToDescriptionToName()
    {
        Assert.Equal("From heading", PlanDocument.Parse("# From heading\n- [ ] a", "name").Title);
        Assert.Equal("From frontmatter",
            PlanDocument.Parse("---\ndescription: From frontmatter\n---\n- [ ] a", "name").Title);
        Assert.Equal("name", PlanDocument.Parse("- [ ] a", "name").Title);
    }

    [Fact]
    public void ADocumentWithoutCheckboxes_IsAPlanWithoutSteps()
    {
        // Never throws on whatever happens to be in .inferpal/plans/ — a half-written file, a note.
        var doc = PlanDocument.Parse("# Just notes\n\nSome prose, no boxes.");

        Assert.Empty(doc.Steps);
        Assert.Null(doc.NextStep);
    }

    // ── The one edit the product may make ──────────────────────────────────────

    [Fact]
    public void TickingAStep_ChangesExactlyOneCharacter()
    {
        // The decision this whole feature rests on: a plan is a human document the product
        // annotates. Re-rendering it would silently eat prose, indentation and comments the user
        // wrote — the artefact the feature exists to keep.
        const string original = """
            ---
            description: keep me
            ---

            # Port the index

            Some prose the user wrote,   with odd   spacing.

            - [ ] Read the C# index
              a continuation line
            - [ ] Add a TS branch

            <!-- a comment -->
            """;

        var updated = PlanDocument.Parse(original).WithStepDone(1, true);

        Assert.NotNull(updated);
        Assert.Equal(original.Replace("- [ ] Read", "- [x] Read"), updated);
    }

    [Fact]
    public void TickingANestedStep_KeepsItsIndentation()
    {
        var updated = PlanDocument.Parse("# T\n\n- [ ] a\n    - [ ] nested\n").WithStepDone(2, true);

        Assert.Contains("    - [x] nested", updated);
    }

    [Fact]
    public void TickingAStepThatIsAlreadyTicked_WritesNothing()
    {
        // null means "nothing to write": the caller must not rewrite a file to change nothing.
        Assert.Null(PlanDocument.Parse("# T\n- [x] done").WithStepDone(1, true));
        Assert.Null(PlanDocument.Parse("# T\n- [x] done").WithStepDone(9, true));
    }

    [Fact]
    public void AStepCanBeUnticked()
    {
        Assert.Contains("- [ ] done", PlanDocument.Parse("# T\n- [X] done").WithStepDone(1, false));
    }

    [Fact]
    public void CrlfFiles_KeepTheirLineEndings_WhenTicked()
    {
        // A plan is a committed human document: rewriting every CRLF to LF on a one-character
        // tick would turn the surgical edit into a whole-file diff. Endings are preserved.
        var updated = PlanDocument.Parse("# T\r\n\r\n- [ ] a\r\n- [ ] b\r\n").WithStepDone(2, true);

        Assert.Equal("# T\r\n\r\n- [ ] a\r\n- [x] b\r\n", updated);
    }

    // ── Rendering a fresh plan ─────────────────────────────────────────────────

    [Fact]
    public void RenderingStripsWhatTheModelAlreadyWrote()
    {
        // A model asked for steps returns "1." and sometimes its own checkbox; doubling either
        // would make the file wrong on its first read-back.
        var text = PlanDocument.Render("Port the index", ["1. Read the C# index", "- [ ] Add a TS branch", "  "]);

        Assert.Equal("# Port the index\n\n- [ ] Read the C# index\n- [ ] Add a TS branch\n", text);
    }

    [Fact]
    public void ARenderedPlan_ReadsBackWithTheSameSteps()
    {
        var doc = PlanDocument.Parse(PlanDocument.Render("T", ["one", "two", "three"]));

        Assert.Equal(["one", "two", "three"], doc.Steps.Select(s => s.Text));
        Assert.Equal(0, doc.DoneCount);
    }

    // ── Name containment ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("../../etc/passwd", "etc-passwd")]
    [InlineData(@"..\..\Windows\System32", "windows-system32")]
    [InlineData("C:/secrets.txt", "c-secrets-txt")]
    [InlineData("feature/login", "feature-login")]
    [InlineData("Port the Index!", "port-the-index")]
    [InlineData("  ", "plan")]
    [InlineData("///", "plan")]
    public void APlanNameCannotLeaveThePlansDirectory(string raw, string expected)
    {
        // The name can come from the model — /plan save names a plan after the task. Traversal is
        // made impossible by construction rather than caught afterwards.
        Assert.Equal(expected, PlanStore.SanitizeName(raw));

        var root = Path.Combine(Path.GetTempPath(), "inferpal-plan-test");
        var path = Path.GetFullPath(PlanStore.PathFor(root, raw));
        Assert.StartsWith(Path.GetFullPath(PlanStore.DirectoryFor(root)) + Path.DirectorySeparatorChar, path);
    }

    // ── Store round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void SaveThenTickThenReload_KeepsEverythingElseIntact()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-plans-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string body = "# Port\n\nNotes kept verbatim.\n\n- [ ] one\n- [ ] two\n";
            PlanStore.Save(root, "Port the Index", body);

            var listed = Assert.Single(PlanStore.List(root));
            Assert.Equal("port-the-index", listed.Name);
            Assert.Equal("Port", listed.Title);
            Assert.Equal(0, listed.Done);
            Assert.Equal(2, listed.Total);
            Assert.False(listed.Complete);

            Assert.NotNull(PlanStore.SetStepDone(root, "port-the-index", 1, true));
            Assert.Null(PlanStore.SetStepDone(root, "port-the-index", 1, true));   // idempotent, no write
            Assert.NotNull(PlanStore.SetStepDone(root, "port-the-index", 2, true));

            var reloaded = PlanStore.Load(root, "port-the-index");
            Assert.Equal(2, reloaded!.DoneCount);
            Assert.Contains("Notes kept verbatim.", reloaded.Text);
            Assert.True(PlanStore.List(root)[0].Complete);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AWorkspaceWithoutPlans_ListsNothingAndLoadsNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-plans-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(PlanStore.List(root));
        Assert.Null(PlanStore.Load(root, "absent"));
        Assert.Null(PlanStore.SetStepDone(root, "absent", 1, true));
    }
}
