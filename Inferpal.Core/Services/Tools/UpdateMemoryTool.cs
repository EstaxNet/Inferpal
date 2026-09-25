using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Execution;
using Inferpal.Services.Signals;

namespace Inferpal.Services.Tools;

/// <summary>
/// Writes the agent's persistent memory (<c>.inferpal/memory.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Approved, confined and snapshotted</b>, and of the three the approval matters most, because
/// of where this file goes. <c>memory.md</c> is read by
/// <c>SystemPromptBuilder</c> and injected into the system prompt of <b>every future session</b>.
/// A tool that writes it unattended is a tool that lets the model edit its own future instructions,
/// permanently, with no human in the loop: the persistence half of a prompt-injection chain, where
/// the content can come from a web page or a file the model was asked to read.
/// </para>
/// <para>
/// Not hypothetical: a hallucinated <c>memory.md</c> written during one run is loaded by the next
/// one, and by every one after it.
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
        // ⚠ Keyword, not GetString: the switch below falls back to "append", so mode='Replace'
        // APPENDED instead of overwriting -- a write silently different from the one asked for, in
        // the file re-injected into the system prompt of every later session. And an unknown mode
        // is refused BEFORE the approval prompt: having the user approve a write we are going to
        // perform differently is worse than refusing it.
        var mode    = args.Keyword("mode") ?? "append";
        if (mode is not ("append" or "replace" or "clear"))
            return $"Unknown mode '{mode}'. Use one of: 'append' (default), 'replace', 'clear'.";

        var content = args.Str("content") ?? string.Empty;

        var projectRoot = ResolveProjectRoot();
        if (string.IsNullOrEmpty(projectRoot))
            return Strings.UpdateMemoryNoProject;

        var ollamaDir = Path.Combine(projectRoot, ".inferpal");
        var memPath   = Path.Combine(ollamaDir, "memory.md");

        // Confined like every other writing tool: without a known workspace root the location comes
        // from the locator's fallbacks, which are a best guess.
        PathSanitizer.AssertUnderRoot(memPath, _getWorkspaceRoot());

        // What the memory's own encoding cannot hold is refused before the prompt, like the other writing tools.
        if (mode != "clear" && FileTarget.EncodingRefusal(memPath, content) is { } cannotHold) return cannotHold;

        // Asked on the path, like every other file tool, so a rule or a force-prompt written for
        // a path covers this write too.
        var details = string.Join(Environment.NewLine, $"{memPath} ({mode})", string.Empty, content);
        if (!await _approval.RequestApprovalAsync(Name, details, ct, subject: memPath))
            return Strings.RunCancelled;

        // Before the write, so /undo-run can put back a memory that "clear" or "replace" removed.
        // ⚠ And through BackUpBeforeChangeAsync, whose remark carries the rule the seven other
        // writing tools honour: "the change must then NOT happen". The direct call to SnapshotAsync
        // threw its answer away, so an impossible snapshot (an unwritable history folder) let `clear`
        // empty the project memory — the one re-injected into the system prompt of every later
        // session — with no net, and the turn answered success.
        var (saved, _) = await _history.BackUpBeforeChangeAsync(memPath, ct);
        if (!saved) return FileHistoryService.BackupFailedMessage(memPath);

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
                // The shared reader: a memory the user wrote in an older editor is in the machine's legacy code page, and
                // read as UTF-8 its accents came back as "�" — then written back over the user's own text.
                var existing = File.Exists(memPath)
                    ? await TextFileEncoding.ReadTextAsync(memPath, ct)
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

    /// <summary>
    /// The root <c>memory.md</c> lives under — the SAME one the system prompt reads it from.
    /// </summary>
    /// <remarks>
    /// ⚠ The prompt reads <c>&lt;projectRoot&gt;/.inferpal/memory.md</c> with the root each front-end
    /// resolves: the workspace root in the host (which is also the index root), and
    /// <see cref="ProjectRootLocator.Locate"/> in the Visual Studio window. A search of its own —
    /// up from the working directory, then from open files — lets the writer and the reader
    /// disagree: in a VS Code workspace with no <c>.sln</c> it climbed into the PARENT folders
    /// (refused as outside the workspace, or "no project"), from an open file it could stop at a
    /// sub-folder the prompt never reads, and in Visual Studio with no document open it found
    /// nothing at all while a solution was open.
    /// </remarks>
    private string ResolveProjectRoot() =>
        _getWorkspaceRoot() is { Length: > 0 } root
            ? root
            : new ProjectRootLocator().Locate(
                _editor.GetOpenDocumentPaths(),
                ActiveSolutionSignal.TryReadSolutionDir(),
                Directory.GetCurrentDirectory());
}
