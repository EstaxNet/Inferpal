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

    public string Description =>
        "Compiles a project or solution and returns the list of errors and warnings. " +
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
        @":\s*(error|warning)\s+\w+\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool OutputHasErrors(string output) =>
        !string.IsNullOrEmpty(output) && _diagLine.IsMatch(output);

    // Errors only — warnings don't warrant auto-fix iterations.
    // Exposed as internal so SmartFixValidator can reuse without duplicating the pattern.
    internal static readonly Regex ErrorLineRegex = new(
        @":\s*error\s+\w+\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool OutputHasBuildErrors(string output) =>
        !string.IsNullOrEmpty(output) && ErrorLineRegex.IsMatch(output);

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rawPath = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        string? path = null;
        if (!string.IsNullOrWhiteSpace(rawPath))
            path = PathSanitizer.Sanitize(rawPath);
        path ??= FindProjectFile();

        if (path is null)
            return Strings.DiagNoProject;

        if (!File.Exists(path))
            return Strings.ToolFileNotFound(path);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(90));

        var psi = new ProcessStartInfo
        {
            FileName               = "dotnet",
            Arguments              = $"build \"{path}\" --no-restore -v minimal",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var process = Process.Start(psi)!;

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        await process.WaitForExitAsync(cts.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = stdout + "\n" + stderr;

        var diagnostics = combined
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => _diagLine.IsMatch(l))
            .Select(l => l.Trim())
            .Distinct()
            .ToList();

        if (diagnostics.Count == 0)
        {
            return process.ExitCode == 0
                ? Strings.DiagBuildOk(Path.GetFileName(path))
                : Strings.DiagBuildFailed(process.ExitCode, stdout.Trim());
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
