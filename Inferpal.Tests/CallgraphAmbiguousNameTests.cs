using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>analyze_code mode=callgraph</c>: a called name defined in several files is said ambiguous, with where — never
/// resolved to whichever definition was indexed first; the analysed file's own definition wins; and a caller list
/// matched by name says when the name is shared.
/// </summary>
/// <remarks>
/// The index kept one definition per name, the first one read. A call to <c>Validate</c> from <c>Zeta.cs</c> showed
/// <c>[Alpha.cs:…]</c> and expanded Alpha's own calls — even when Zeta defined its own <c>Validate</c> — a wrong tree,
/// and the footer counted it "resolved across files".
/// </remarks>
public sealed class CallgraphAmbiguousNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-callamb-" + Guid.NewGuid().ToString("N"));

    public CallgraphAmbiguousNameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string name, string body)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, body);
        return path;
    }

    private Task<string> Analyze(object args) =>
        new TraceDependencyTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Fact]
    public async Task ANameDefinedInTwoOtherFiles_IsAmbiguous_NotTheFirstOne()
    {
        Write("Alpha.cs", "class Alpha\n{\n    public void Validate() { AlphaOnly(); }\n    void AlphaOnly() { }\n}\n");
        Write("Beta.cs",  "class Beta\n{\n    public void Validate() { BetaOnly(); }\n    void BetaOnly() { }\n}\n");
        var caller = Write("Checkout.cs", "class Checkout\n{\n    public void Run(Alpha a) { a.Validate(); }\n}\n");

        var report = await Analyze(new { path = caller, symbol = "Run", direction = "callees", depth = 3 });

        Assert.Contains("Validate()  [ambiguous: defined in 2 files — ", report, StringComparison.Ordinal);
        Assert.DoesNotContain("AlphaOnly", report, StringComparison.Ordinal);   // not expanded as if it were Alpha's
    }

    [Fact]
    public async Task TheAnalysedFilesOwnDefinition_Wins()
    {
        Write("Alpha.cs", "class Alpha\n{\n    public void Validate() { AlphaOnly(); }\n    void AlphaOnly() { }\n}\n");
        var zeta = Write("Zeta.cs", "class Zeta\n{\n    public void Run() { Validate(); }\n    void Validate() { ZetaOnly(); }\n    void ZetaOnly() { }\n}\n");

        var report = await Analyze(new { path = zeta, symbol = "Run", direction = "callees", depth = 3 });

        Assert.Contains("Validate()  [this file:", report, StringComparison.Ordinal);
        Assert.Contains("ZetaOnly", report, StringComparison.Ordinal);
        Assert.DoesNotContain("AlphaOnly", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUniqueName_ResolvesAsBefore()
    {
        // Reference arm: one definition, one answer.
        Write("Alpha.cs", "class Alpha\n{\n    public void Validate() { AlphaOnly(); }\n    void AlphaOnly() { }\n}\n");
        var caller = Write("Checkout.cs", "class Checkout\n{\n    public void Run(Alpha a) { a.Validate(); }\n}\n");

        var report = await Analyze(new { path = caller, symbol = "Run", direction = "callees", depth = 3 });

        Assert.Contains("Validate()  [Alpha.cs:3]", report, StringComparison.Ordinal);
        Assert.DoesNotContain("ambiguous", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallerListByName_SaysWhenTheNameIsShared()
    {
        var alpha = Write("Alpha.cs", "class Alpha\n{\n    public void Validate() { }\n}\n");
        Write("Beta.cs", "class Beta\n{\n    public void Validate() { }\n}\n");
        Write("Checkout.cs", "class Checkout\n{\n    public void Run(Beta b) { b.Validate(); }\n}\n");

        var report = await Analyze(new { path = alpha, symbol = "Validate", direction = "both" });

        Assert.Contains("Run()", report, StringComparison.Ordinal);   // witness: the by-name caller is listed
        Assert.Contains("⚠ Validate is defined in 2 files: a caller listed here may call another of them.", report, StringComparison.Ordinal);
    }
}
