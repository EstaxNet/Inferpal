using System.Diagnostics;
using System.IO;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An MSBuild binary log reads as its errors first, its warnings, the projects it built and what took longest — replayed
/// by the .NET SDK on this machine, no reader shipped.
/// </summary>
public class BinlogTextTests
{
    // What `dotnet msbuild <log> -flp:v=minimal;performancesummary` writes, cut to its shape.
    private const string Replay = """
          Determining projects to restore...
          All projects are up-to-date for restore.
        C:\ws\App\Program.cs(1,19): error CS0103: The name 'undefinedThing' does not exist in the current context [C:\ws\App\App.csproj]
        C:\ws\Lib\Lib.cs(4,9): warning CS0168: The variable 'e' is declared but never used [C:\ws\Lib\Lib.csproj]
        C:\ws\App\Program.cs(1,19): error CS0103: The name 'undefinedThing' does not exist in the current context [C:\ws\App\App.csproj]
          Lib -> C:\ws\Lib\bin\Debug\net10.0\Lib.dll

        Project Performance Summary:
             2310 ms  C:\ws\App\App.csproj                       1 calls
              840 ms  C:\ws\Lib\Lib.csproj                       1 calls

        Target Performance Summary:
                0 ms  BeforeBuild                                1 calls
              120 ms  ResolveAssemblyReferences                  2 calls
             1900 ms  CoreCompile                                2 calls

        Task Performance Summary:
                3 ms  Message                                    4 calls
             1880 ms  Csc                                        2 calls
        """;

    [Fact]
    public void AReplay_ReadsErrorsFirst_ThenWarnings_Outputs_AndTheSlowest()
    {
        var text = BinlogText.Summarize(Replay).Text;

        Assert.StartsWith(BinlogText.Heading, text);
        var errors = text.IndexOf("## Errors (1)", StringComparison.Ordinal);          // logged twice, said once
        var warnings = text.IndexOf("## Warnings (1)", StringComparison.Ordinal);
        Assert.True(errors > 0 && warnings > errors);
        Assert.Contains("error CS0103", text[errors..warnings]);
        Assert.Contains("Lib -> C:\\ws\\Lib\\bin\\Debug\\net10.0\\Lib.dll", text);
        // The slowest first, with its calls; the restore's chatter is not an output.
        Assert.Contains("## Slowest targets (of 3)\n1900 ms  CoreCompile (2 calls)\n120 ms  ResolveAssemblyReferences", text.Replace("\r\n", "\n"));
        Assert.Contains("## Slowest projects (of 2)\n2310 ms  C:\\ws\\App\\App.csproj (1 call)", text.Replace("\r\n", "\n"));
        Assert.Contains("1880 ms  Csc (2 calls)", text);
        Assert.DoesNotContain("Determining projects", text);
    }

    [Fact]
    public void ACleanBuild_SaysNone_NeverAnEmptyList()
    {
        var text = BinlogText.Summarize("  Lib -> C:\\ws\\Lib.dll\n").Text;
        Assert.Contains("## Errors (0)\n(none)", text.Replace("\r\n", "\n"));
        Assert.Contains("## Warnings (0)\n(none)", text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ABinaryLog_IsADocument_EveryReaderOpens()
    {
        Assert.True(DocumentText.Handles("build.binlog"));
        Assert.True(DocumentText.ToolsRead("build.binlog"));
        Assert.Equal(64L * 1024 * 1024, DocumentText.MaxBytesOf("x.binlog"));
        Assert.Equal(DocumentText.BuildLogDeadline, DocumentText.DeadlineOf("x.binlog"));
    }

    [Fact]
    public async Task ARealBrokenBuild_ReplaysToItsError()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-binlog-test-").FullName;
        try
        {
            // The SDK's own framework, so nothing is downloaded: a test machine may be offline.
            File.WriteAllText(Path.Combine(dir, "Broken.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType>"
                + "<TargetFramework>net$(NETCoreAppMaximumVersion)</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dir, "Program.cs"), "System.Console.WriteLine(undefinedThing);\n");
            var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = dir };
            foreach (var a in new[] { "build", "-bl:build.binlog", "-v:q", "-nologo" }) psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            var build = await Inferpal.Services.ChildProcess.RunAsync(psi, TimeSpan.FromMinutes(3), CancellationToken.None);
            Assert.False(build.TimedOut, "the build did not finish");
            var binlog = Path.Combine(dir, "build.binlog");
            Assert.True(File.Exists(binlog), "no binary log written: " + build.Stdout + build.Stderr);   // WITNESS

            var read = DocumentText.Extract(binlog);

            Assert.Equal(DocumentProblem.None, read.Problem);
            Assert.StartsWith(BinlogText.Heading, read.Text);
            Assert.Contains("CS0103", read.Text);
            Assert.Contains("## Slowest targets", read.Text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* cleanup */ }
        }
    }
}
