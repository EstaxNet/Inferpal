using System.IO;
using System.Text;

namespace Inferpal.Services.Persistence;

/// <summary>
/// Write-then-rename for the small JSON stores Inferpal keeps under <c>%AppData%</c>
/// (configuration, sessions, snippets, bench and arena results, MCP tokens).
/// </summary>
/// <remarks>
/// A plain <c>File.WriteAllText</c> truncates the target before writing: a crash, a full disk or a
/// kill between the two leaves a half-written file — and for the configuration that means the
/// extension can no longer start. Staging into a sibling <c>.tmp</c> and renaming makes the
/// replacement atomic on both NTFS and POSIX, so a reader ever only sees the old file or the
/// new one.
/// </remarks>
internal static class AtomicFile
{
    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="content"/>.</summary>
    public static void WriteAllText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    /// <inheritdoc cref="WriteAllText(string,string)"/>
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, Encoding.UTF8, ct);
        File.Move(temp, path, overwrite: true);
    }

    /// <inheritdoc cref="WriteAllText(string,string)"/>
    public static void WriteAllBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
