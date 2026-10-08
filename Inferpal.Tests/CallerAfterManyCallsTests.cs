using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>analyze_code</c>: a method that calls the target after many other calls is still its caller.
/// </summary>
/// <remarks>
/// The call extractor stopped at 30 distinct called names per method, and the caller search read that capped list: a
/// method calling the target after its first 30 calls (logging, string building, collection calls — common names are
/// not filtered) was not listed, and the report said "no callers found in scanned files" over a fully scanned
/// workspace. The cap now applies to the callee tree's DISPLAY only, with what it leaves out counted.
/// </remarks>
public sealed class CallerAfterManyCallsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-callers-" + Guid.NewGuid().ToString("N"));
    private readonly string _target;
    private readonly string _caller;

    public CallerAfterManyCallsTests()
    {
        Directory.CreateDirectory(_root);
        _target = Path.Combine(_root, "Pricing.cs");
        File.WriteAllText(_target, "class Pricing\n{\n    public static int ComputeTotal(int a) { return a; }\n}\n");
        var calls = string.Concat(Enumerable.Range(1, 32).Select(i => $"        Step{i}();\n"));
        _caller = Path.Combine(_root, "Checkout.cs");
        File.WriteAllText(_caller,
            "class Checkout\n{\n    public static void Run()\n    {\n" + calls
            + "        var total = Pricing.ComputeTotal(3);\n    }\n}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private Task<string> Analyze(string path, string direction) =>
        new TraceDependencyTool(() => _root).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path, direction }), CancellationToken.None);

    [Fact]
    public async Task AMethodThatCallsTheTargetAfterThirtyOtherCalls_IsItsCaller()
    {
        var report = await Analyze(_target, "callers");

        Assert.Contains("ComputeTotal", report, StringComparison.Ordinal);               // witness: the target was read
        Assert.Contains("Run", report, StringComparison.Ordinal);
        Assert.DoesNotContain("no callers found", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCalleeTree_ShowsThirty_AndCountsTheRest()
    {
        var report = await Analyze(_caller, "callees");

        Assert.Contains("+3 more call(s) not shown", report, StringComparison.Ordinal);   // 33 calls, 30 shown
        Assert.Contains("Step1()", report, StringComparison.Ordinal);
    }
}
