using System.Text.Json.Serialization;

namespace Inferpal.Services.CodeActions;

/// <summary>One line of an editor's problem list — Visual Studio's Error List, VS Code's Problems panel — chosen to fix.</summary>
/// <param name="File">The file, absolute; <c>null</c> for a problem that names none (a command-line or tool error).</param>
/// <param name="Line">1-based; 0 when the problem names a file and no line (a NuGet warning on a project).</param>
/// <param name="Column">1-based; 0 when unknown.</param>
/// <param name="Severity"><c>error</c>, <c>warning</c> or <c>info</c>.</param>
/// <param name="Code">The diagnostic's code (<c>CS0103</c>, <c>NU1903</c>, <c>2304</c>); <c>null</c> when it has none.</param>
/// <param name="Text">The message.</param>
/// <param name="Source">Who reported it (<c>ts</c>, <c>eslint</c>), when the editor says; <c>null</c> otherwise.</param>
/// <remarks>Shared in source with the in-process assembly, which reads Visual Studio's Error List.</remarks>
internal sealed record ProblemEntry(
    [property: JsonPropertyName("file")]     string? File,
    [property: JsonPropertyName("line")]     int     Line,
    [property: JsonPropertyName("column")]   int     Column,
    [property: JsonPropertyName("severity")] string  Severity,
    [property: JsonPropertyName("code")]     string? Code,
    [property: JsonPropertyName("text")]     string  Text,
    [property: JsonPropertyName("source")]   string? Source = null);
