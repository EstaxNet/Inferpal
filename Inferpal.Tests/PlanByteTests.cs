using System.IO;
using System.Text;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>PlanDocument.WithStepDone</c> promises that "only the single checkbox character changes;
/// every other byte of the file is preserved" — and the promise is about the FILE, so it is the
/// write that has to keep it. A plan is markdown a team commits: a byte that appears at the head
/// of the file shows up as a diff nobody asked for.
/// </summary>
public class PlanByteTests : IDisposable
{
    private readonly string _ws;

    public PlanByteTests()
    {
        _ws = Path.Combine(Path.GetTempPath(), "inferpal-tests", "plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_ws, ".inferpal", "plans"));
    }

    public void Dispose() { try { Directory.Delete(_ws, true); } catch { /* best effort */ } }

    private const string PlanText = "# Ship it\n\n- [ ] first step\n- [ ] second step\n";

    private string PlanPath => Path.Combine(_ws, ".inferpal", "plans", "ship-it.md");

    [Fact]
    public void TickingAStep_DoesNotAddAByteAtTheHeadOfAHandWrittenPlan()
    {
        // A plan the user wrote (or a team committed): plain UTF-8, no byte-order mark.
        File.WriteAllBytes(PlanPath, Encoding.UTF8.GetBytes(PlanText).AsSpan().ToArray());
        var before = File.ReadAllBytes(PlanPath);
        Assert.False(before[0] == 0xEF, "the fixture must start without a BOM.");

        PlanStore.SetStepDone(_ws, "ship-it", 1, true);

        var after = File.ReadAllBytes(PlanPath);
        Assert.False(after.Length >= 3 && after[0] == 0xEF && after[1] == 0xBB && after[2] == 0xBF,
            "ticking a step added a byte-order mark to a file that had none.");

        // And the promise in full: exactly one character differs.
        var b = Encoding.UTF8.GetString(before);
        var a = Encoding.UTF8.GetString(after);
        Assert.Equal(b.Length, a.Length);
        Assert.Equal(1, b.Zip(a).Count(p => p.First != p.Second));
    }

    [Fact]
    public void TickingAStep_KeepsTheMarkOfAPlanThatHadOne()
    {
        // Witness on the other side: a plan written WITH a mark keeps it — the rule is "preserve",
        // not "strip".
        File.WriteAllBytes(PlanPath, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(PlanText)]);

        PlanStore.SetStepDone(_ws, "ship-it", 1, true);

        var after = File.ReadAllBytes(PlanPath);
        Assert.True(after.Length >= 3 && after[0] == 0xEF && after[1] == 0xBB && after[2] == 0xBF,
            "ticking a step stripped the byte-order mark of a file that had one.");
    }

    [Fact]
    public void TickingAStep_KeepsCrLfLineEndings()
    {
        // The other half of "every other byte": WithStepDone rebuilds the line, so the CR of a
        // CRLF file must come back with it.
        File.WriteAllBytes(PlanPath, Encoding.UTF8.GetBytes(PlanText.Replace("\n", "\r\n")));

        PlanStore.SetStepDone(_ws, "ship-it", 1, true);

        var text = File.ReadAllText(PlanPath);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
    }
}
