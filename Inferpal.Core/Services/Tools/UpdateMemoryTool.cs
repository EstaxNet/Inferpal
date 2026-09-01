using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Tools;

/// <summary>
/// Writes the agent's persistent memory (<c>.inferpal/memory.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Approved, confined and snapshotted since the post-1.6.1 review</b>, and of the three that
/// matters most is the approval — because of where this file goes. <c>memory.md</c> is read by
/// <c>SystemPromptBuilder</c> and injected into the system prompt of <b>every future session</b>.
/// A tool that writes it unattended is a tool that lets the model edit its own future instructions,
/// permanently, with no human in the loop: the persistence half of a prompt-injection chain, where
/// the content can come from a web page or a file the model was asked to read.
/// </para>
/// <para>
/// This is not hypothetical here. The §25 validation runs found a hallucinated <c>memory.md</c>
/// left inside the guinea-pig repository, and the run after it started with that memory loaded —
/// the harness was fixed to clean it, which is the right fix for a harness and no fix at all for
/// the product.
/// </para>
/// <para>
/// <c>mode: "clear"</c> and <c>"replace"</c> also destroy what the user accumulated, which is
/// what the snapshot is for.
/// </para>
/// </remarks>
internal class UpdateMemoryTool : ITool
{
    private readonly IEditorSurface _editor;
    private readonly IApprovalService _approval;
    private readonly FileHistoryService _history;
    private readonly Func<string> _getWorkspaceRoot;

    public UpdateMemoryTool(IEditorSurface editor, IApprovalService approval,
                            FileHistoryService history, Func<string> getWorkspaceRoot)
    {
        _editor           = editor;
        _approval         = approval;
        _history          = history;
        _getWorkspaceRoot = getWorkspaceRoot;
    }

    public string Name => "update_memory";

    public string Description =>
        "Updates the agent's persistent memory stored in .inferpal/memory.md. " +
        "Use mode='append' (default) to add a new note, 'replace' to rewrite the entire memory, " +
        "or 'clear' to erase it. " +
        "The memory is automatically injected into every future system prompt, so anything noted " +
        "here persists across sessions. Ideal for architecture decisions, user preferences, " +
        "resolved bugs, and recurring patterns.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            content = new
            {
                type        = "string",
                description = "Text to write. Required for append and replace, ignored for clear."
            },
            mode = new
            {
                type        = "string",
                description = "append (default): add content after existing notes. replace: overwrite everything. clear: erase all memory."
            }
        },
        required = Array.Empty<string>(),
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var mode    = args.TryGetProperty("mode",    out var m) ? m.GetString() ?? "append" : "append";
        var content = args.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;

        var projectRoot = FindProjectRoot();
        if (projectRoot is null)
            return Strings.UpdateMemoryNoProject;

        var ollamaDir = Path.Combine(projectRoot, ".inferpal");
        var memPath   = Path.Combine(ollamaDir, "memory.md");

        // The project root is found by walking up from the CWD and from open editor paths, so it
        // is a guess — one that can land outside the workspace. Every other writing tool is
        // confined; this one was not.
        PathSanitizer.AssertUnderRoot(memPath, _getWorkspaceRoot());

        // Asked on the path, like every other file tool, so a rule or a force-prompt written for
        // a path covers this write too.
        var details = string.Join(Environment.NewLine, $"{memPath} ({mode})", string.Empty, content);
        if (!await _approval.RequestApprovalAsync(Name, details, ct, subject: memPath))
            return Strings.RunCancelled;

        // Before the write, so /undo-run can put back a memory that "clear" or "replace" removed.
        if (File.Exists(memPath)) await _history.SnapshotAsync(memPath, ct);

        Directory.CreateDirectory(ollamaDir);

        string newContent;
        switch (mode)
        {
            case "clear":
                newContent = string.Empty;
                await SafeFileWriter.WritePreservingAsync(memPath, newContent, ct);
                return Strings.UpdateMemoryClear(memPath);

            case "replace":
                if (string.IsNullOrWhiteSpace(content))
                    return Strings.UpdateMemoryNoContent;
                newContent = content;
                break;

            default: // append
                if (string.IsNullOrWhiteSpace(content))
                    return Strings.UpdateMemoryNoContent;
                var existing = File.Exists(memPath)
                    ? await File.ReadAllTextAsync(memPath, Encoding.UTF8, ct)
                    : string.Empty;
                newContent = string.IsNullOrWhiteSpace(existing)
                    ? content
                    : existing.TrimEnd() + "\n\n" + content;
                break;
        }

        // SafeFileWriter: memory.md is committable and user-editable — keep whatever
        // encoding/BOM the user's editor gave it instead of forcing UTF-8 with BOM.
        await SafeFileWriter.WritePreservingAsync(memPath, newContent, ct);
        return Strings.UpdateMemoryOk(memPath, newContent.Length);
    }

    // Walks up from CWD and then from open editor files, looking for a .sln or .inferpal dir.
    // CWD is often wrong in an out-of-process VS extension, hence the open-path fallback.
    private string? FindProjectRoot()
    {
        // 1. Walk up from CWD
        var dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 8; i++)
        {
            if (Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Length > 0) return dir;
            if (Directory.Exists(Path.Combine(dir, ".inferpal")))                        return dir;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null || parent == dir) break;
            dir = parent;
        }

        // 2. Walk up from any open editor file
        foreach (var p in _editor.GetOpenDocumentPaths())
        {
            var d = Path.GetDirectoryName(p);
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(d); i++)
            {
                if (Directory.GetFiles(d, "*.sln", SearchOption.TopDirectoryOnly).Length > 0) return d;
                if (Directory.Exists(Path.Combine(d, ".inferpal")))                        return d;
                d = Directory.GetParent(d)?.FullName;
            }
        }

        return null;
    }
}
