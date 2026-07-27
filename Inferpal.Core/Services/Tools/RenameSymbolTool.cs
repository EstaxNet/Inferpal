using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Inferpal.Services.Rag;

namespace Inferpal.Services.Tools;

/// <summary>
/// Project-wide symbol rename: replaces every occurrence of an identifier across source files.
/// Uses Roslyn syntax analysis for C# (zero false matches inside strings or comments) and
/// word-boundary regex for other languages.  Supports a dry-run preview before committing.
/// </summary>
internal sealed class RenameSymbolTool : ITool
{
    private readonly IApprovalService   _approval;
    private readonly FileHistoryService _history;
    private readonly Func<string?>      _getRoot;

    public RenameSymbolTool(IApprovalService approval, FileHistoryService history, Func<string?> getRoot)
    {
        _approval = approval;
        _history  = history;
        _getRoot  = getRoot;
    }

    public string Name => "rename_symbol";

    public string Description =>
        "Project-wide symbol rename: replaces every occurrence of an identifier across all source files " +
        "under 'root'. Uses Roslyn for C# (no false matches in strings or comments) and word-boundary " +
        "regex for other languages. Always call with dry_run=true first to preview, then dry_run=false to apply.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            root = new
            {
                type        = "string",
                description = "Root directory to search in. Defaults to the solution root when omitted."
            },
            old_name = new
            {
                type        = "string",
                description = "Current identifier name (exact, case-sensitive)."
            },
            new_name = new
            {
                type        = "string",
                description = "New identifier name to replace it with."
            },
            file_pattern = new
            {
                type        = "string",
                description = "Restrict rename to files matching this glob pattern (e.g. '*.cs', '*.ts'). Defaults to all supported source extensions."
            },
            dry_run = new
            {
                type        = "boolean",
                description = "true (default) = preview only, no changes written. false = apply after approval."
            }
        },
        required = new[] { "old_name", "new_name" }
    };

    // ── ExecuteAsync ───────────────────────────────────────────────────────────

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var root        = PathSanitizer.Sanitize(
                              (args.TryGetProperty("root", out var rv) ? rv.GetString() : null)
                              ?? _getRoot());
        // rename_symbol WRITES files — keep it inside the workspace like write_file/apply_diff.
        PathSanitizer.AssertUnderRoot(root, _getRoot());
        var oldName     = args.GetProperty("old_name").GetString()?.Trim() ?? string.Empty;
        var newName     = args.GetProperty("new_name").GetString()?.Trim() ?? string.Empty;
        var filePattern = args.TryGetProperty("file_pattern", out var fp) ? fp.GetString() : null;
        var dryRun      = !args.TryGetProperty("dry_run", out var dr) || dr.GetBoolean();

        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
            return "old_name and new_name are required.";
        if (oldName == newName)
            return "old_name and new_name are identical — nothing to do.";
        if (!IsValidIdentifier(newName))
            return $"'{newName}' is not a valid identifier name.";
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return $"Directory not found: '{root}'. Provide a valid 'root' parameter.";

        // ── Enumerate files ────────────────────────────────────────────────────
        var files = EnumerateSourceFiles(root, filePattern);
        if (files.Count == 0)
            return $"No source files found under '{root}'.";

        // ── Scan for occurrences ───────────────────────────────────────────────
        var hits            = new List<(string FilePath, int Count, string NewContent)>();
        int totalOccurrences = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var content = await File.ReadAllTextAsync(file, ct);

                var (newContent, count) = Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                    ? RenameInCSharp(content, oldName, newName)
                    : RenameWithRegex(content, oldName, newName);

                if (count > 0)
                {
                    hits.Add((file, count, newContent));
                    totalOccurrences += count;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip unreadable files */ }
        }

        if (hits.Count == 0)
            return $"No occurrences of `{oldName}` found in {files.Count} scanned file(s).";

        // ── Preview report ─────────────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine($"## rename_symbol: `{oldName}` → `{newName}`");
        sb.AppendLine($"Found **{totalOccurrences}** occurrence(s) in **{hits.Count}** file(s) (scanned {files.Count}):");
        sb.AppendLine();

        foreach (var (f, count, _) in hits.OrderBy(h => h.FilePath))
            sb.AppendLine($"- `{Path.GetRelativePath(root, f)}` — {count} occurrence(s)");

        if (dryRun)
        {
            sb.AppendLine();
            sb.AppendLine("*Dry run — no files modified. Call again with `dry_run: false` to apply.*");
            return sb.ToString().TrimEnd();
        }

        // ── Apply with approval ────────────────────────────────────────────────
        var details = $"Rename `{oldName}` → `{newName}` in {hits.Count} file(s) ({totalOccurrences} occurrence(s))";
        // Subject for permission rules = every affected file path (newline-separated) so allow/deny
        // path patterns are matched against the actual files, not the localized summary. A deny rule
        // matching ANY affected path blocks the whole rename (the policy short-circuits on first match).
        var subject = string.Join("\n", hits.Select(h => h.FilePath));
        if (!await _approval.RequestApprovalAsync("rename_symbol", details, ct, subject: subject))
            return "Rename cancelled by user.";

        var errors = new List<string>();
        foreach (var (filePath, _, newContent) in hits)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _history.SnapshotAsync(filePath, ct);
                await File.WriteAllTextAsync(filePath, newContent, ct);
            }
            catch (Exception ex)
            {
                errors.Add($"  {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        sb.AppendLine();
        if (errors.Count == 0)
            sb.AppendLine($"✅ Applied to {hits.Count} file(s). Use `restore_file` to undo individual files.");
        else
            sb.AppendLine($"⚠ Applied with {errors.Count} error(s):\n{string.Join('\n', errors)}");

        return sb.ToString().TrimEnd();
    }

    // ── Roslyn rename (C# only) ────────────────────────────────────────────────

    private static (string newContent, int count) RenameInCSharp(
        string content, string oldName, string newName)
    {
        var root = CSharpSyntaxTree.ParseText(content).GetRoot();

        var tokens = root.DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == oldName)
            .ToList();

        if (tokens.Count == 0) return (content, 0);

        var newRoot = root.ReplaceTokens(tokens, (original, _) =>
            SyntaxFactory.Identifier(
                original.LeadingTrivia,
                newName,
                original.TrailingTrivia));

        return (newRoot.ToFullString(), tokens.Count);
    }

    // ── Regex rename (other languages) ─────────────────────────────────────────

    private static (string newContent, int count) RenameWithRegex(
        string content, string oldName, string newName)
    {
        // Build a word-boundary pattern for this specific name
        var pattern = $@"(?<!\w){Regex.Escape(oldName)}(?!\w)";
        int count   = 0;
        var result  = Regex.Replace(content, pattern, _ => { count++; return newName; });
        return (result, count);
    }

    // ── File enumeration ───────────────────────────────────────────────────────

    private static List<string> EnumerateSourceFiles(string rootDir, string? filePattern)
    {
        var result = new List<string>();
        try
        {
            if (filePattern is not null)
            {
                // User-specified glob: enumerate with that pattern, then filter by extension
                foreach (var f in Directory.EnumerateFiles(rootDir, filePattern, SearchOption.AllDirectories))
                {
                    if (!IsExcluded(f) &&
                        CodeChunker.SupportedExtensions.Contains(Path.GetExtension(f)) &&
                        new FileInfo(f).Length < CodeChunker.MaxFileSizeBytes)
                        result.Add(f);
                }
            }
            else
            {
                foreach (var ext in CodeChunker.SupportedExtensions)
                {
                    foreach (var f in Directory.EnumerateFiles(rootDir, $"*{ext}", SearchOption.AllDirectories))
                    {
                        if (!IsExcluded(f) && new FileInfo(f).Length < CodeChunker.MaxFileSizeBytes)
                            result.Add(f);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("RenameSymbolTool.ScanFile", ex); }
        return result;
    }

    private static bool IsExcluded(string path) =>
        path.Contains(@"\obj\",          StringComparison.Ordinal) ||
        path.Contains(@"\bin\",          StringComparison.Ordinal) ||
        path.Contains(@"\.git\",         StringComparison.Ordinal) ||
        path.Contains(@"\node_modules\", StringComparison.Ordinal) ||
        path.Contains(@"\.vs\",          StringComparison.Ordinal) ||
        path.Contains(@"\dist\",         StringComparison.Ordinal) ||
        path.Contains("/obj/",           StringComparison.Ordinal) ||
        path.Contains("/bin/",           StringComparison.Ordinal) ||
        path.Contains("/.git/",          StringComparison.Ordinal) ||
        path.Contains("/node_modules/",  StringComparison.Ordinal);

    private static bool IsValidIdentifier(string name) =>
        !string.IsNullOrEmpty(name) &&
        (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_');
}
