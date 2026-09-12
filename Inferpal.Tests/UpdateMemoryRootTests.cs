using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Editor;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>update_memory</c> writes <c>memory.md</c> where the system prompt reads it back — and nowhere
/// else.
/// </summary>
/// <remarks>
/// The tool looked for its root its own way (up from the working directory, then from open files),
/// different from both readers. The tests exercising it had to <b>change the process's working
/// directory</b> for it to work: something the product never does, and which hid the defect.
/// </remarks>
public sealed class UpdateMemoryRootTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-memroot-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* cleanup */ }
    }

    private sealed class NoEditor : IEditorSurface
    {
        public bool IsAvailable => false;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) => Task.FromResult<ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) => Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class Approve : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
            string? subject = null, Inferpal.Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false) =>
            Task.FromResult(true);
    }

    private static JsonElement Args(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    private UpdateMemoryTool Tool() =>
        new(new NoEditor(), new Approve(), new Inferpal.Services.Execution.FileHistoryService(), () => _root);

    [Fact]
    public async Task WritesUnderTheWorkspaceRoot_WithoutASolutionOrAnInferpalFolder()
    {
        // Witness: the working directory is not the root — no SetCurrentDirectory here.
        Assert.NotEqual(Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar),
                        Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar));

        await Tool().ExecuteAsync(Args(new { content = "prefer small commits" }), CancellationToken.None);

        var memory = Path.Combine(_root, ".inferpal", "memory.md");
        Assert.True(File.Exists(memory), "memory.md was not written under the workspace root.");
        Assert.Contains("prefer small commits", await File.ReadAllTextAsync(memory), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatItWrites_IsWhatTheSystemPromptReads()
    {
        // The invariant between the writer and the reader: writing a memory the prompt does not read
        // back succeeds silently, and the agent forgets it next session.
        await Tool().ExecuteAsync(Args(new { content = "REMEMBER-THIS-4217" }), CancellationToken.None);

        var prompt = new Inferpal.Services.Prompting.SystemPromptBuilder(new InferpalConfig())
            .Build(Strings.SystemPrompt, projectRoot: _root);

        Assert.Contains("REMEMBER-THIS-4217", prompt, StringComparison.Ordinal);
    }
}
