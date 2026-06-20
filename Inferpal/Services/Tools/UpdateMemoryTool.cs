using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class UpdateMemoryTool : ITool
{
    private readonly VsContextHolder _contextHolder;

    public UpdateMemoryTool(VsContextHolder contextHolder)
    {
        _contextHolder = contextHolder;
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
        Directory.CreateDirectory(ollamaDir);

        string newContent;
        switch (mode)
        {
            case "clear":
                newContent = string.Empty;
                await File.WriteAllTextAsync(memPath, newContent, Encoding.UTF8, ct);
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

        await File.WriteAllTextAsync(memPath, newContent, Encoding.UTF8, ct);
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
        foreach (var p in _contextHolder.GetOpenPaths())
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
