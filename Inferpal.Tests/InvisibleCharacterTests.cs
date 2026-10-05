using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ No source file carries a raw control character (tab, CR and LF aside). Invisible on screen, one reads as nothing:
/// <c>"\u0001ro\u0001"</c> typed raw reads as the letters "ro" — a key a tool name could share — and a sentinel typed raw
/// reads as <c>""</c>, an empty string that <c>Replace</c> would refuse. The usual way in is a tool that decodes an
/// escape while writing a file (<c>\u0001</c> becoming the character, <c>tools\verify…</c> becoming a vertical tab and
/// "erify…"), so the scan is the only reader that notices. A control character the code needs is written as an escape.
/// </summary>
public class InvisibleCharacterTests
{
    private static readonly string[] Folders =
        ["Inferpal.Core", "Inferpal", "Inferpal.Host", "Inferpal.InProc", "Inferpal.Fim", "Inferpal.Tests", "vscode/src", "tools"];

    private static readonly string[] Extensions = [".cs", ".ts", ".xaml", ".resx", ".csproj", ".props", ".ps1"];

    private static IEnumerable<string> Sources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Inferpal.sln"))) root = root.Parent;
        Assert.NotNull(root);

        var top = Directory.EnumerateFiles(root!.FullName, "*.ps1", SearchOption.TopDirectoryOnly);
        var nested = Folders
            .Select(f => Path.Combine(root.FullName, f.Replace('/', Path.DirectorySeparatorChar)))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "node_modules"));
        return top.Concat(nested);
    }

    [Fact]
    public void NoSource_CarriesARawControlCharacter()
    {
        var files = Sources().ToList();
        Assert.True(files.Count > 500, $"only {files.Count} source file(s) read: the scan reads nothing");   // WITNESS

        var found = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var bad = lines[i].FirstOrDefault(c => char.IsControl(c) && c != '\t');
                if (bad != default)
                    found.Add($"{Path.GetFileName(file)}:{i + 1} (U+{(int)bad:X4})");
            }
        }

        Assert.True(found.Count == 0,
            "Raw control character(s) — invisible in the source; write them as an escape (\\u0001):\n" + string.Join("\n", found));
    }
}
