using System.IO;
using Inferpal.Config;
using Inferpal.Services.Shell;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Windows PowerShell run with <c>-EncodedCommand</c> (how every shell call is made, for quoting's sake) takes its caller
/// for another PowerShell and writes its non-output streams to stderr as CLIXML. Every command then came back to the
/// model with a "[stderr]" section — the look of a failure — holding ~700 characters of XML for a progress record
/// ("Preparing modules for first use", emitted at start-up), and a real cmdlet error came as
/// <c>&lt;S S="Error"&gt;Get-Item : …_x000D__x000A_&lt;/S&gt;</c>, one element per line. Measured on this machine.
/// </summary>
public class PowerShellStderrTests
{
    // The exact shapes measured (French Windows PowerShell 5.1).
    private const string ProgressOnly =
        "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\" RefId=\"0\"><TN RefId=\"0\"><T>System.Management.Automation.PSCustomObject</T><T>System.Object</T></TN><MS><I64 N=\"SourceId\">1</I64><PR N=\"Record\"><AV>Préparation des modules à la première utilisation.</AV><AI>0</AI><Nil /><PI>-1</PI><PC>-1</PC><T>Completed</T><SR>-1</SR><SD> </SD></PR></MS></Obj></Objs>";

    private const string WithError =
        "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\" RefId=\"0\"><TN RefId=\"0\"><T>System.Management.Automation.PSCustomObject</T><T>System.Object</T></TN><MS><I64 N=\"SourceId\">1</I64><PR N=\"Record\"><AV>Préparation des modules à la première utilisation.</AV><AI>0</AI><Nil /><PI>-1</PI><PC>-1</PC><T>Completed</T><SR>-1</SR><SD> </SD></PR></MS></Obj>"
        + "<S S=\"Error\">Get-Item : Impossible de trouver le chemin d'accès « C:\\NoSuchFolderZzz », car il n'existe pas._x000D__x000A_</S>"
        + "<S S=\"Error\">    + CategoryInfo          : ObjectNotFound: (C:\\NoSuchFolderZzz:String) [Get-Item], ItemNotFoundException_x000D__x000A_</S></Objs>";

    [Fact]
    public void AProgressRecord_IsNotStderr()
    {
        Assert.Equal(string.Empty, PowerShellStderr.Decode(ProgressOnly).Trim());
    }

    [Fact]
    public void AnErrorRecord_IsItsText()
    {
        var text = PowerShellStderr.Decode(WithError);

        Assert.Contains("Get-Item : Impossible de trouver le chemin d'accès « C:\\NoSuchFolderZzz », car il n'existe pas.", text);
        Assert.Contains("+ CategoryInfo", text);
        Assert.DoesNotContain("CLIXML", text);
        Assert.DoesNotContain("<S ", text);
        Assert.DoesNotContain("_x000D_", text);
        Assert.DoesNotContain("Préparation des modules", text);
    }

    [Fact]
    public void ANativeStderrLine_InterleavedWithTheXml_IsKept()
    {
        // `cmd /c dir C:\missing` under PowerShell: cmd's own stderr line lands between the header and the XML.
        var text = PowerShellStderr.Decode("#< CLIXML\r\n Fichier introuvable\r\n" + ProgressOnly["#< CLIXML\r\n".Length..]);

        Assert.Equal("Fichier introuvable", text.Trim());
    }

    [Fact]
    public void OrdinaryStderr_AndUnreadableXml_AreKeptAsTheyAre()
    {
        // Reference arms: a POSIX shell's stderr, and an element that does not parse, are never lost.
        Assert.Equal("fatal: not a git repository", PowerShellStderr.Decode("fatal: not a git repository"));
        const string broken = "<Objs Version=\"1.1.0.1\"><S S=\"Error\">cut";
        Assert.Contains(broken, PowerShellStderr.Decode("#< CLIXML\n" + broken));
    }

    /// <summary>
    /// Every site that starts a process through the shell launcher decodes its stderr — derived from the code, not
    /// listed: background jobs, custom tools and Smart Fix read the same stream as run_command.
    /// </summary>
    [Fact]
    public void EverySiteThatLaunchesTheShell_DecodesItsStderr()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var launchers = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "Inferpal.Core"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (File: f, Code: ConventionCoverageTests.CodeOnly(f)))
            .Where(x => x.Code.Contains("ShellLauncher.BuildStartInfo(", StringComparison.Ordinal))
            .ToList();

        Assert.True(launchers.Count >= 4, $"only {launchers.Count} launch site(s) found: the scan reads nothing");   // WITNESS
        var silent = launchers.Where(x => !x.Code.Contains("PowerShellStderr.Decode", StringComparison.Ordinal))
                              .Select(x => Path.GetFileName(x.File)).ToList();
        Assert.True(silent.Count == 0, "stderr read raw (CLIXML reaches the model) in: " + string.Join(", ", silent));
    }

    // ── End to end, the real shell (Windows PowerShell is where this happens) ─

    /// <summary>
    /// A redirected Windows PowerShell formats objects to its hidden console's width, 120: a table was cut to
    /// "aaa…" and a column past the width was DROPPED — no marker says so. The realistic case is
    /// <c>Get-ChildItem -Recurse | Select-Object FullName</c>, whose long paths came back cut, then used as paths.
    /// </summary>
    [Fact]
    public async Task AFormattedObject_IsNotCutAtTheConsoleWidth_AndTheSessionStillPersists()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = new ShellSession(() => Path.GetTempPath(), new InferpalConfig());

        var table = await shell.RunAsync("[pscustomobject]@{ Name = ('a' * 150) + 'TAIL'; Value = 'END' } | Format-Table",
                                         null, CancellationToken.None);
        Assert.Contains("Name", table);                                                           // witness
        Assert.Contains("TAIL", table);
        Assert.Contains("END", table);                                                            // the dropped column

        // ⚠ A wide buffer PADS the default table views to its width: four files came back as 20 570 characters of
        // spaces. What reaches the model has no trailing blanks.
        var listing = await shell.RunAsync("Get-ChildItem C:\\Windows | Select-Object -First 4", null, CancellationToken.None);
        Assert.Contains("Mode", listing);                                                         // witness
        Assert.True(listing.Split('\n').Max(l => l.TrimEnd('\r').Length) < 300,
                    $"a line of {listing.Split('\n').Max(l => l.Length)} characters: the table is padded to the buffer");

        // Reference arm: the wrapper's pipeline must not cost the session its state.
        await shell.RunAsync("cd C:\\Windows; $env:INFERPAL_PROBE = 'kept'", null, CancellationToken.None);
        var state = await shell.RunAsync("(Get-Location).Path; $env:INFERPAL_PROBE", null, CancellationToken.None);
        Assert.Contains("C:\\Windows", state);
        Assert.Contains("kept", state);
    }

    /// <summary>
    /// A native tool that writes UTF-8 — git (commit messages, <c>git diff</c> of an accented line), dotnet's localized
    /// messages — came back decoded in the console's OEM code page: "entières" read "enti├¿res", "exécution"
    /// "ex├®cution" (measured on this machine). The model then quotes the mangled line into an edit.
    /// </summary>
    [Fact]
    public async Task ANativeToolWritingUtf8_ReadsAsItWrote_AndPowerShellsOwnTextStillDoes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir  = Directory.CreateTempSubdirectory("inferpal-utf8-").FullName;
        var file = Path.Combine(dir, "utf8.txt");
        File.WriteAllBytes(file, [.. "café crème"u8, (byte)'\r', (byte)'\n']);
        var shell = new ShellSession(() => dir, new InferpalConfig());
        try
        {
            // cmd's `type` writes the file's bytes as they are: UTF-8 straight through, then through a pipeline.
            Assert.Contains("café crème", await shell.RunAsync($"cmd /c type \"{file}\"", null, CancellationToken.None));
            Assert.Contains("café crème",
                await shell.RunAsync($"cmd /c type \"{file}\" | Select-Object -First 1", null, CancellationToken.None));

            // Reference arms: what reads right today must still.
            Assert.Contains("café", await shell.RunAsync("Write-Output 'café'", null, CancellationToken.None));
            Assert.Contains("café", await shell.RunAsync("cmd /c echo café", null, CancellationToken.None));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* cleanup */ } }
    }

    [Fact]
    public async Task ASucceedingCommand_HasNoStderrSection_AndAFailingOneReadsPlainly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = new ShellSession(() => Path.GetTempPath(), new InferpalConfig());

        var ok = await shell.RunAsync("Write-Output 'ok'", null, CancellationToken.None);
        Assert.StartsWith("ok", ok.TrimStart());                                                   // witness
        Assert.DoesNotContain("CLIXML", ok);
        Assert.DoesNotContain("[stderr]", ok);

        var failed = await shell.RunAsync("Get-Item C:\\NoSuchFolderInferpalZzz", null, CancellationToken.None);
        Assert.Contains("[stderr]", failed);
        Assert.Contains("NoSuchFolderInferpalZzz", failed);
        Assert.DoesNotContain("CLIXML", failed);
        Assert.DoesNotContain("<S S=", failed);
    }
}
