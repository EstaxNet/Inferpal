using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  Starting anew empties the auto-save slot — this workspace's, never another's.
//
//  /clear and "New conversation" archived the conversation under a name and left the auto-save slot
//  full: the next start of either editor brought the discarded conversation back — and in VS Code
//  so did every host restart (a setting, a crash), the blank screen reading as "nothing to keep".
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class ClearForgetsAutoSaveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-forget-").FullName;
    private static readonly string Here  = Path.Combine(Path.GetTempPath(), "workspace-here");
    private static readonly string Other = Path.Combine(Path.GetTempPath(), "workspace-other");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task ThisWorkspacesConversation_LeavesTheSlot()
    {
        var store = new ConversationStore(_dir);
        await store.AutoSaveAsync([new SavedMessage("user", "the discarded question")], CancellationToken.None, Here);

        await store.ForgetAutoSaveAsync(Here, CancellationToken.None);

        var slot = await store.LoadAsync("last_session", CancellationToken.None);
        Assert.NotNull(slot);   // witness: the slot is read, not missing
        Assert.Empty(slot!.Messages);
    }

    [Fact]
    public async Task AnotherWorkspacesConversation_StaysInTheSlot()
    {
        // Reference arm: the slot is one file for every project and both editors.
        var store = new ConversationStore(_dir);
        await store.AutoSaveAsync([new SavedMessage("user", "elsewhere")], CancellationToken.None, Other);

        await store.ForgetAutoSaveAsync(Here, CancellationToken.None);

        var slot = await store.LoadAsync("last_session", CancellationToken.None);
        Assert.Single(slot!.Messages);
    }

    [Theory]
    [InlineData("Inferpal.Host", "HostServer.cs", "StartNewConversation", "ForgetAutoSaveAsync")]
    [InlineData("Inferpal", "ToolWindow/InferpalToolWindowData.Connection.cs", "ClearAsync", "ForgetAutoSaveAsync")]
    public void BothFrontEnds_ForgetTheSlot_WhenTheConversationIsDiscarded(string project, string file, string method,
                                                                             string call)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, project, Path.Combine(file.Split('/')));
        var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
        var body = System.Linq.Enumerable.SingleOrDefault(
            System.Linq.Enumerable.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>(root.DescendantNodes()),
            m => m.Identifier.Text == method);
        Assert.True(body is not null, $"{method} was not found in {file}: this test would measure nothing.");

        var code = string.Concat(System.Linq.Enumerable.Select(body!.DescendantTokens(), t => t.Text + " "));
        Assert.Contains(call, code, StringComparison.Ordinal);
    }
}
