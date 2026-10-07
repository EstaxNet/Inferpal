using System.IO;
using Inferpal.Services.Lsp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>rename_symbol</c>'s <c>declaring_file</c> written as the workspace path with a leading separator —
/// <c>/src/Shop/Rules/IPriceRule.cs</c> — designates that file, as <c>src/Shop/Rules/IPriceRule.cs</c> does.
/// </summary>
/// <remarks>
/// ⚠ Compared as a full path, it matched no declaration: Devstral, told the name designated two symbols, sent this
/// narrowing three times, was refused three times, and the run stopped for repeating itself.
/// </remarks>
public class DeclaringFileLeadingSlashTests
{
    private static readonly string Declaring =
        Path.Combine(Path.GetTempPath(), "ws", "src", "Shop", "Rules", "IPriceRule.cs");

    [Theory]
    [InlineData("/src/Shop/Rules/IPriceRule.cs")]
    [InlineData("/Rules/IPriceRule.cs")]
    public void ALeadingSeparator_NamesTheTrailingComponents(string written) =>
        Assert.True(CSharpSemanticIndex.IsDeclaringFile(Declaring, written));

    /// <summary>⚠ Reference arms: the relative and absolute forms still match, and a name that is not on a component
    /// boundary, or another file, does not.</summary>
    [Fact]
    public void OtherForms_AndOtherFiles_AreUnchanged()
    {
        Assert.True(CSharpSemanticIndex.IsDeclaringFile(Declaring, "src/Shop/Rules/IPriceRule.cs"));
        Assert.True(CSharpSemanticIndex.IsDeclaringFile(Declaring, Declaring));
        Assert.False(CSharpSemanticIndex.IsDeclaringFile(Declaring, "/ules/IPriceRule.cs"));
        Assert.False(CSharpSemanticIndex.IsDeclaringFile(Declaring, "/src/Shop/Coupon.cs"));
        Assert.False(CSharpSemanticIndex.IsDeclaringFile(Declaring, Path.Combine(Path.GetTempPath(), "other", "IPriceRule.cs")));
    }
}
