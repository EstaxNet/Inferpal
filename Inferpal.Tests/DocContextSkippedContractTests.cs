using System.IO;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A contract that was FOUND and not read is not a contract that lives elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// The two render the same way — no "Interface contracts" section at all — and the model then
/// writes its own summary for members it could have inherited with <c>&lt;inheritdoc/&gt;</c>.
/// An interface that simply lives in another project is the ordinary case and must stay silent;
/// a file sitting right there, matched by name and deliberately skipped, is not.
/// </para>
/// <para>
/// ⚠ The rule is already written two screens up in the same file, for the member cap: "+N more
/// member(s)", under a comment saying a twenty-member interface shown as twelve made the model
/// document "the interface" believing it had seen all of it. This is the same rule one level
/// higher, where the whole contract disappears instead of its tail.
/// </para>
/// </remarks>
public sealed class DocContextSkippedContractTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"inferpal-doccx-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Impl = """
        namespace App;
        public class Widget : IWidget
        {
            public void Draw() { }
        }
        """;

    private const string Iface = """
        namespace App;
        public interface IWidget
        {
            /// <summary>Draws the widget on the current surface.</summary>
            void Draw();
        }
        """;

    private async Task<string> BlockAsync(string name, string? iface)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;
        var impl = Path.Combine(dir, "Widget.cs");
        File.WriteAllText(impl, Impl);
        if (iface is not null) File.WriteAllText(Path.Combine(dir, "IWidget.cs"), iface);
        return await DocContextExtractor.BuildContextBlockAsync(impl, Impl, CancellationToken.None);
    }

    [Fact]
    public async Task AContractThatIsReadable_IsListed_AndNothingIsReportedAsSkipped()
    {
        // The reference arm: without it, every assertion below is satisfied by a block that has
        // stopped reporting contracts at all.
        var block = await BlockAsync("ok", Iface);

        Assert.Contains("**Interface contracts**", block, StringComparison.Ordinal);
        Assert.Contains("void Draw()", block, StringComparison.Ordinal);
        Assert.DoesNotContain("contract not read", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInterfaceThatLivesElsewhere_StaysSILENT()
    {
        // The ordinary case, and the reason this cannot be a blanket notice: most interfaces of a
        // solution are not siblings of their implementation, and saying so for each would be noise.
        var block = await BlockAsync("elsewhere", iface: null);

        Assert.Contains("class Widget : IWidget", block, StringComparison.Ordinal);
        Assert.DoesNotContain("contract not read", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContractFileOverTheSizeCap_IsNamed_RatherThanRenderedAsAbsent()
    {
        var block = await BlockAsync("toobig", Iface + "\n" + new string('/', 21_000));

        // WITNESS: the block was produced and still describes the type — the assertion below is
        // about what it says of the contract, not about a block that never happened.
        Assert.Contains("class Widget : IWidget", block, StringComparison.Ordinal);

        Assert.Contains("contract not read", block, StringComparison.Ordinal);
        Assert.Contains("IWidget.cs", block, StringComparison.Ordinal);
        Assert.Contains("KB", block, StringComparison.Ordinal);
    }
}
