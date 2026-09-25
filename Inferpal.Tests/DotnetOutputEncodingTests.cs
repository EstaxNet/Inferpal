using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The .NET SDK writes its output in UTF-8; <c>get_diagnostics</c> started <c>dotnet build</c> without saying how to
/// decode it, so the host's console code page decided. On a French machine every compiler error carried "┬á" — the
/// non-breaking space French typography puts before a colon (<c>'C.F(bool)'┬á: les chemins du code…</c>) — and
/// accented messages came back mangled: the text <c>get_diagnostics</c>, <c>/fix-build</c> and "Fix with AI" hand
/// the model. Measured on this machine; an English SDK writes ASCII, so elsewhere this test only runs the build.
/// </summary>
public sealed class DotnetOutputEncodingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-diag-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task ACompilerErrorThroughGetDiagnostics_ReadsAsTheSdkWroteIt()
    {
        var project = Path.Combine(_dir, "p.csproj");
        File.WriteAllText(project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType>"
          + "<TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_dir, "C.cs"), "class C { static int F(bool b) { if (b) return 1; } }");   // CS0161
        using (var restore = Process.Start(new ProcessStartInfo("dotnet", $"restore \"{project}\"")
               { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!)
        {
            _ = restore.StandardOutput.ReadToEndAsync();
            _ = restore.StandardError.ReadToEndAsync();
            await restore.WaitForExitAsync();
        }

        var report = await new GetDiagnosticsTool(getRoot: () => _dir)
            .ExecuteAsync(JsonDocument.Parse(JsonSerializer.Serialize(new { path = project })).RootElement,
                          CancellationToken.None);

        Assert.Contains("CS0161", report);                                                         // witness
        foreach (var mojibake in new[] { "┬", "├", "Â", "Ã", "�" })   // ┬ ├ Â Ã �
            Assert.DoesNotContain(mojibake, report);
    }
}
