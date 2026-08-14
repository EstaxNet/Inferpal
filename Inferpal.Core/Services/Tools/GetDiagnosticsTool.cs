using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class GetDiagnosticsTool : ITool
{
    internal const string ToolName = "get_diagnostics";
    public string Name => ToolName;

    private readonly Services.Editor.IEditorSurface? _editor;

    /// <param name="editor">When the editor exposes live language-service diagnostics,
    /// they are returned instantly instead of building; null / no diagnostics falls
    /// back to the compile flow (VS today).</param>
    public GetDiagnosticsTool(Services.Editor.IEditorSurface? editor = null) => _editor = editor;

    public string Description =>
        "Returns current errors and warnings. Uses the editor's live diagnostics when " +
        "available (instant, open files); otherwise compiles the project or solution. " +
        "If path is omitted, looks for the first .sln or .csproj in the current directory. " +
        "Timeout: 90 seconds.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type        = "string",
                description = "Path to the .sln or .csproj file (optional)."
            }
        },
        required = Array.Empty<string>(),
    };

    private static readonly Regex _diagLine = new(
        @":\s*(error|warning)\s+\w+\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default);

    internal static bool OutputHasErrors(string output) =>
        !string.IsNullOrEmpty(output) && _diagLine.IsMatch(output);

    // Errors only — warnings don't warrant auto-fix iterations.
    // Exposed as internal so SmartFixValidator can reuse without duplicating the pattern.
    internal static readonly Regex ErrorLineRegex = new(
        @":\s*error\s+\w+\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default);

    internal static bool OutputHasBuildErrors(string output) =>
        !string.IsNullOrEmpty(output) && ErrorLineRegex.IsMatch(output);

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rawPath = args.TryGetProperty("path", out var p) ? p.GetString() : null;

        // Editor fast path: live language-service diagnostics beat a 90 s build, but only
        // when the model didn't ask for a specific project (an explicit path means "build
        // THAT") and the editor actually reports problems (a clean panel proves nothing
        // about unopened files — fall through to the compile).
        if (_editor is not null && string.IsNullOrWhiteSpace(rawPath))
        {
            var live = await _editor.GetEditorDiagnosticsAsync(ct);
            if (!string.IsNullOrWhiteSpace(live))
                return live!.Trim();
        }

        string? path = null;
        if (!string.IsNullOrWhiteSpace(rawPath))
            path = PathSanitizer.Sanitize(rawPath);
        path ??= FindProjectFile();

        if (path is null)
            return Strings.DiagNoProject;

        if (!File.Exists(path))
            return Strings.ToolFileNotFound(path);

        var psi = new ProcessStartInfo
        {
            FileName  = "dotnet",
            Arguments = $"build \"{path}\" --no-restore -v minimal",
        };

        // 90 s, after which the build tree is killed — it used to be abandoned, and MSBuild node
        // processes outlived the turn that started them.
        var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(90), ct);
        var stdout   = run.Stdout;
        var combined = run.Combined;

        var diagnostics = combined
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => _diagLine.IsMatch(l))
            .Select(l => l.Trim())
            .Distinct()
            .ToList();

        if (diagnostics.Count == 0)
        {
            return run.Succeeded
                ? Strings.DiagBuildOk(Path.GetFileName(path))
                : Strings.DiagBuildFailed(run.ExitCode, stdout.Trim());
        }

        var errors   = diagnostics.Count(d => _diagLine.Match(d).Groups[1].Value.Equals("error",   StringComparison.OrdinalIgnoreCase));
        var warnings = diagnostics.Count(d => _diagLine.Match(d).Groups[1].Value.Equals("warning", StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder();
        sb.AppendLine(Strings.DiagSummary(errors, warnings, Path.GetFileName(path)));
        sb.AppendLine();
        foreach (var d in diagnostics)
            sb.AppendLine(d);

        return sb.ToString().Trim();
    }

    private static string? FindProjectFile()
    {
        var cwd = Directory.GetCurrentDirectory();
        foreach (var ext in new[] { "*.sln", "*.csproj" })
        {
            var found = Directory.GetFiles(cwd, ext, SearchOption.AllDirectories)
                                 .FirstOrDefault();
            if (found is not null) return found;
        }
        return null;
    }
}
