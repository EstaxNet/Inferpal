using System.ComponentModel;
using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A build check that could not run says so.
//
//  Smart Fix caught any failure to run its validator with a bare `catch { return null; }` — the answer of "no
//  validator for this file". The edit then read as checked: no note for the model, no trace in /diagnostics.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]
public sealed class SmartFixCouldNotRunTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-smartfix-crash-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string CSharpFile()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project />");
        var file = Path.Combine(_root, "Program.cs");
        File.WriteAllText(file, "class C { }");
        return file;
    }

    [Fact]
    public async Task AValidatorThatCannotStart_IsSaid_NeverTakenForNoValidator()
    {
        var file = CSharpFile();
        var smartFix = new SmartFixValidator(new InferpalConfig { SmartFixEnabled = true }, () => _root)
        {
            Run = (_, _, _) => throw new Win32Exception("The directory name is invalid."),
        };

        var note = await smartFix.ValidateAsync(file, CancellationToken.None);

        Assert.Equal(Strings.SmartFixCouldNotRun("The directory name is invalid."), note);
        Assert.Contains(Diagnostics.Snapshot(), e => e.Context == "SmartFixValidator.Run");
    }

    [Fact]
    public async Task AValidatorThatRuns_StillReportsItsBuild()
    {
        // Reference arm: a check that ran says what it found, as before.
        var file = CSharpFile();
        var smartFix = new SmartFixValidator(new InferpalConfig { SmartFixEnabled = true }, () => _root)
        {
            Run = (_, _, _) => Task.FromResult((0, "Build succeeded.", false)),
        };

        Assert.Equal(Strings.SmartFixBuildOk, await smartFix.ValidateAsync(file, CancellationToken.None));
    }
}
