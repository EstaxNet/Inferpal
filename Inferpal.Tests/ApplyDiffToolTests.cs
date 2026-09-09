using System.IO;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What <c>apply_diff</c> does with a call the model wrote badly.
/// </summary>
/// <remarks>
/// <para>
/// <c>new_content</c> is declared <b>required</b> in the schema, and was read as
/// <c>args.Str("new_content") ?? ""</c>. Omitting it therefore did not fail — it became a
/// <b>deletion</b> of the matched block, and the tool answered "diff applied". The model reads a
/// success for a call it wrote wrong, and carries on as if its replacement text were in the file.
/// </para>
/// <para>
/// The empty string stays valid: it is how a block is deleted. What changed is that it now has to
/// be <i>written</i>, which is the difference between an intention and an accident.
/// </para>
/// </remarks>
public class ApplyDiffToolTests
{
    private sealed class StubApproval(bool approve) : IApprovalService
    {
        public int Calls { get; private set; }

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null,
                                               bool forcePrompt = false)
        {
            Calls++;
            return Task.FromResult(approve);
        }
    }

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Json(string s) => JsonSerializer.Serialize(s);

    [Fact]
    public async Task MissingNewContent_IsRefusedByName_NotTreatedAsADeletion()
    {
        var dir  = Directory.CreateTempSubdirectory("inferpal-diff").FullName;
        var file = Path.Combine(dir, "A.cs");
        await File.WriteAllTextAsync(file, "int x = 1;\n");
        try
        {
            var approval = new StubApproval(approve: true);
            var tool     = new ApplyDiffTool(approval, new FileHistoryService(), () => dir);

            // ToolRegistry turns a thrown ArgumentException into "Tool 'x' error: <message>", so the
            // model reads the sentence either way; what matters is that it NAMES the argument.
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => tool.ExecuteAsync(
                Raw($$"""{"path":{{Json(file)}},"old_content":"int x = 1;"}"""), CancellationToken.None));

            Assert.Contains("new_content", ex.Message, StringComparison.Ordinal);
            Assert.Equal("int x = 1;\n", await File.ReadAllTextAsync(file));
            Assert.Equal(0, approval.Calls);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task AnEmptyNewContent_StaysAValidDeletion()
    {
        var dir  = Directory.CreateTempSubdirectory("inferpal-diff").FullName;
        var file = Path.Combine(dir, "A.cs");
        await File.WriteAllTextAsync(file, "keep\ndrop\n");
        try
        {
            var tool = new ApplyDiffTool(new StubApproval(approve: true), new FileHistoryService(), () => dir);

            await tool.ExecuteAsync(
                Raw($$"""{"path":{{Json(file)}},"old_content":"drop\n","new_content":""}"""),
                CancellationToken.None);

            Assert.Equal("keep\n", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
