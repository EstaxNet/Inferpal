using System.IO;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  /test extends a test file in that file's own line endings and final line break.
//
//  The model answers in LF without a final break, and both editors replace the whole test file with
//  the answer: a CRLF file (Visual Studio's default) came back with every line changed and no final
//  line break. write_file, apply_diff and the code actions keep the file's convention; this path did not.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class TestGenerationLineEndingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-testeol-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static FakeInferenceProvider Model(string answer) =>
        new() { ChatResult = new ChatTurnResult(answer, null, 0, 0) };

    private async Task<TestGenerationPlan> Extend(string existing, string answer)
    {
        var source = Path.Combine(_dir, "Widget.cs");
        File.WriteAllText(source, "public class Widget { public int Add(int a, int b) => a + b; }");
        var test = TestFilePathResolver.Resolve(source);
        Directory.CreateDirectory(Path.GetDirectoryName(test)!);
        File.WriteAllText(test, existing);

        var plan = await TestGenerationPlanner.PlanAsync(
            Model(answer), "m", source, File.ReadAllText(source), CancellationToken.None);
        Assert.True(plan.Ok && plan.Extended, "The witness: the existing file is extended.");
        return plan;
    }

    private const string Answer = "public class WidgetTests\n{\n    [Fact] public void A() { }\n    [Fact] public void B() { }\n}";

    [Fact]
    public async Task ACrlfTestFile_IsExtendedInCrlf_WithItsFinalLineBreak()
    {
        var plan = await Extend("public class WidgetTests\r\n{\r\n    [Fact] public void A() { }\r\n}\r\n", Answer);

        Assert.Equal(Answer.Replace("\n", "\r\n") + "\r\n", plan.Content);
    }

    [Fact]
    public async Task AnLfTestFileWithoutAFinalBreak_StaysThatWay()
    {
        // Reference arm: an LF file without a final break is the model's own shape — nothing is added.
        var plan = await Extend("public class WidgetTests\n{\n    [Fact] public void A() { }\n}", Answer);

        Assert.Equal(Answer, plan.Content);
    }
}
