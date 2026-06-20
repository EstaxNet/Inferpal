using System.IO;
using System.Text.Json;

namespace Inferpal.Services;

/// <summary>
/// A post-write build/typecheck validator for one ecosystem: which file extensions trigger it,
/// which marker files locate the project root (walked up from the edited file), and the command to
/// run there. The command runs in the resolved project directory; the literal <c>{project}</c> token
/// is replaced with the matched marker file's path (used by the .NET validator to target a specific
/// <c>.csproj</c>/<c>.sln</c>).
/// </summary>
internal sealed record BuildValidator(
    string Name,
    IReadOnlyList<string> Extensions,   // lower-case, leading dot (".cs")
    IReadOnlyList<string> Markers,      // Directory.GetFiles globs, in preference order ("*.csproj", "go.mod")
    string Command,
    bool UseDotnetErrorFilter = false); // .NET: parse ": error XX:" lines; others: rely on the exit code

/// <summary>
/// Resolves which <see cref="BuildValidator"/> applies to an edited file and where its project root
/// is. Pure/static and filesystem-injectable so the selection + walk-up logic is unit-testable
/// without touching disk. The previous .NET-only Smart Fix behaviour is preserved by the built-in
/// <c>dotnet</c> validator; other ecosystems are sensible defaults, overridable/extendable via the
/// workspace <c>.inferpal/validators.json</c> overlay.
/// </summary>
internal static class BuildValidators
{
    // Built-in defaults. The .NET entry reproduces the historical behaviour (nearest .csproj, else
    // .sln; targeted dotnet build). The others are common, widely-available toolchain commands; a
    // missing toolchain is reported as "not installed" and stays silent (see SmartFixValidator).
    public static IReadOnlyList<BuildValidator> Defaults { get; } =
    [
        new("dotnet",
            [".cs", ".csproj", ".sln", ".fs", ".fsproj", ".vb", ".vbproj", ".props", ".targets", ".razor", ".xaml"],
            ["*.csproj", "*.sln"],
            "dotnet build \"{project}\" --no-restore -v minimal",
            UseDotnetErrorFilter: true),
        new("typescript", [".ts", ".tsx", ".mts", ".cts"], ["tsconfig.json"], "npx --no-install tsc --noEmit"),
        new("rust",       [".rs"],                          ["Cargo.toml"],    "cargo check --quiet"),
        new("go",         [".go"],                          ["go.mod"],        "go build ./..."),
    ];

    /// <summary>
    /// Parses the <c>.inferpal/validators.json</c> overlay: an object keyed by a comma-separated list
    /// of extensions, each value an object with <c>marker</c> (string or array) and <c>command</c>.
    /// <code>{ ".ts,.tsx": { "marker": "tsconfig.json", "command": "npx tsc --noEmit" } }</code>
    /// Returns an empty list on missing/invalid JSON (never throws).
    /// </summary>
    public static IReadOnlyList<BuildValidator> ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];

            var list = new List<BuildValidator>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                var exts = prop.Name
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
                    .ToList();
                if (exts.Count == 0) continue;

                if (!prop.Value.TryGetProperty("command", out var cmdEl) || cmdEl.ValueKind != JsonValueKind.String)
                    continue;
                var command = cmdEl.GetString();
                if (string.IsNullOrWhiteSpace(command)) continue;

                var markers = new List<string>();
                if (prop.Value.TryGetProperty("marker", out var mk))
                {
                    if (mk.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mk.GetString()))
                        markers.Add(mk.GetString()!);
                    else if (mk.ValueKind == JsonValueKind.Array)
                        markers.AddRange(mk.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                            .Select(x => x.GetString()!));
                }
                if (markers.Count == 0) continue;   // a marker is required to locate the project root

                list.Add(new BuildValidator(exts[0], exts, markers, command!));
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The validators to consult, overlay first so a user/team entry overrides a built-in default for
    /// the same extension (selection in <see cref="Match"/> picks the first matching validator).
    /// </summary>
    public static IReadOnlyList<BuildValidator> Resolve(IReadOnlyList<BuildValidator> overlay) =>
        [.. overlay, .. Defaults];

    /// <summary>
    /// Picks the validator for <paramref name="filePath"/>'s extension and walks up from its directory
    /// to the nearest ancestor containing a marker file. <paramref name="findMarker"/> returns the
    /// first file matching a glob in a directory (or <c>null</c>) — injected so the walk-up is testable
    /// without disk. Returns <c>null</c> when no validator matches the extension or no marker is found.
    /// </summary>
    public static (BuildValidator Validator, string ProjectDir, string ProjectFile)? Match(
        string filePath, IReadOnlyList<BuildValidator> validators, Func<string, string, string?> findMarker)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;

        var validator = validators.FirstOrDefault(
            v => v.Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)));
        if (validator is null) return null;

        var dir = Path.GetDirectoryName(filePath);
        for (int depth = 0; depth < 12 && !string.IsNullOrEmpty(dir); depth++)
        {
            foreach (var marker in validator.Markers)
            {
                var found = findMarker(dir, marker);
                if (!string.IsNullOrEmpty(found))
                    return (validator, dir, found!);
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
