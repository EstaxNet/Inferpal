using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  rename_symbol given QUALIFIED names says so and names the bare identifiers.
//
//  "Rename the method Cart.ComputeTotal to CalculateTotal" leads a model to old_name
//  "Cart.ComputeTotal", new_name "Cart.CalculateTotal". "'Cart.CalculateTotal' is not a valid
//  identifier name" names no remedy: the model blames the name the user chose and stops. Naming the
//  bare identifiers is what makes it retry. Nothing is renamed on the model's behalf.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class RenameQualifiedNameTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("inferpal-qualified-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class Yes : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) =>
            Task.FromResult(true);
    }

    private async Task<string> RenameAsync(string oldName, string newName)
    {
        File.WriteAllText(Path.Combine(_ws, "Cart.cs"),
            "namespace Shop;\n\npublic sealed class Cart\n{\n    public decimal ComputeTotal() => 0m;\n}\n");
        var args = JsonDocument.Parse(JsonSerializer.Serialize(new { old_name = oldName, new_name = newName })).RootElement.Clone();
        return await new RenameSymbolTool(new Yes(), new FileHistoryService(), () => _ws).ExecuteAsync(args, default);
    }

    [Theory]
    [InlineData("Cart.ComputeTotal", "Cart.CalculateTotal", "'Cart.CalculateTotal' is a qualified name")]
    [InlineData("Cart.ComputeTotal", "CalculateTotal",      "'Cart.ComputeTotal' is a qualified name")]
    [InlineData("Shop::Cart::ComputeTotal", "CalculateTotal", "'Shop::Cart::ComputeTotal' is a qualified name")]
    public async Task AQualifiedName_IsRefused_WithTheBareIdentifiersToSend(string oldName, string newName, string opening)
    {
        var result = await RenameAsync(oldName, newName);

        Assert.StartsWith(opening, result, StringComparison.Ordinal);
        Assert.Contains("old_name `ComputeTotal`, new_name `CalculateTotal`", result, StringComparison.Ordinal);
        Assert.Contains("ComputeTotal()", File.ReadAllText(Path.Combine(_ws, "Cart.cs")));   // nothing renamed
    }

    [Fact]
    public async Task MovingAMemberToAnotherType_IsNotARename()
    {
        var result = await RenameAsync("Cart.ComputeTotal", "Receipt.ComputeTotal");

        Assert.Contains("does not move a member to another type", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BareNames_AreRenamed_AndAnInvalidName_KeepsItsOwnRefusal()
    {
        // Reference arms: the ordinary call is untouched, and a name that is not qualified keeps its message.
        Assert.DoesNotContain("qualified", await RenameAsync("ComputeTotal", "CalculateTotal"), StringComparison.Ordinal);
        Assert.Equal("'Calculate Total' is not a valid identifier name.", await RenameAsync("ComputeTotal", "Calculate Total"));
    }
}
