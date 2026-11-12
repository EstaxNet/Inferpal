using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>analyze_code</c>: a method that calls the target from the target's own file is its caller. Skipped, the report said
/// "no callers found in scanned files" for a call one line below the target.
/// </summary>
public sealed class CallerInTheSameFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-same-file-caller-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ACallerInTheTargetsOwnFile_IsListed()
    {
        var file = Path.Combine(_root, "Calc.cs");
        File.WriteAllText(file,
            "public static class Arith\n{\n    public static int Add(int a, int b) => a + b;\n}\n\n"
            + "public static class Program\n{\n    public static void Main() => System.Console.WriteLine(Arith.Add(2, 3));\n}\n");

        var report = await new TraceDependencyTool(() => _root).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = file, symbol = "Add", direction = "callers" }), CancellationToken.None);

        Assert.Contains("• Main()  [Calc.cs:", report, StringComparison.Ordinal);
    }
}
