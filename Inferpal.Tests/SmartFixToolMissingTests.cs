using Inferpal.Localization;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Shell;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Smart Fix stays silent when the toolchain itself is missing, and must say every real build error. It told them
/// apart by WORDS in the output — two ways wrong. "No such file", "could not be found", "ENOENT" are how a C compiler
/// reports a missing header, rustc a missing include_str!, a build script a missing file: real errors, silenced. And
/// the shell's own "command not found" is translated: an English/French list read a missing toolchain as a failed
/// build on every write in any other language. The signals kept say it in no language: exit 127 (POSIX shells), 9009
/// (cmd), PowerShell's error id <c>CommandNotFoundException</c>, and npm's own English-only sentences. Outputs are
/// real (Windows PowerShell 5.1 in French, pwsh 7.4, bash, npm 10, node 24, go 1.23) except the German PowerShell one,
/// which is its resource text.
/// </summary>
public class SmartFixToolMissingTests
{
    [Theory]
    // Windows PowerShell 5.1, French — the message is translated, the id is not.
    [InlineData(1, "nosuchtoolxyz : Le terme «nosuchtoolxyz» n'est pas reconnu comme nom d'applet de commande, fonction, fichier de script ou programme exécutable.\nAu caractère Ligne:2 : 1\n+ nosuchtoolxyz --version\n    + CategoryInfo          : ObjectNotFound: (nosuchtoolxyz:String) [], CommandNotFoundException\n    + FullyQualifiedErrorId : CommandNotFoundException\n")]
    // Windows PowerShell 5.1, German.
    [InlineData(1, "npx : Die Benennung \"npx\" wurde nicht als Name eines Cmdlet, einer Funktion, einer Skriptdatei oder eines ausführbaren Programms erkannt.\n    + CategoryInfo          : ObjectNotFound: (npx:String) [], CommandNotFoundException\n    + FullyQualifiedErrorId : CommandNotFoundException\n")]
    // pwsh 7.4 under NormalView (what Smart Fix asks for), colours included.
    [InlineData(1, "\u001b[31;1mnosuchtoolxyz : \u001b[31;1mThe term 'nosuchtoolxyz' is not recognized as a name of a cmdlet, function, script file, or executable program.\u001b[0m\n\u001b[31;1m\u001b[31;1m+ CategoryInfo          : ObjectNotFound: (nosuchtoolxyz:String) [], CommandNotFoundException\u001b[0m\n")]
    // bash — in English and German; the exit code is the same.
    [InlineData(127, "bash: line 1: nosuchtool: command not found\n")]
    [InlineData(127, "bash: Zeile 1: npx: Befehl nicht gefunden.\n")]
    // npx asked for a tsc the project has not installed.
    [InlineData(1, "npm error npx canceled due to missing packages and no YES option: [\"tsc@2.0.4\"]\nnpm error A complete log of this run can be found in: C:\\x\\debug-0.log\n")]
    public void AMissingToolchain_StaysSilent_InAnyLanguage(int exitCode, string output)
    {
        Assert.Null(SmartFixValidator.Interpret(exitCode, output, dotnetFilter: false));
    }

    [Theory]
    // A build script that reads a file that is not there (node 24).
    [InlineData("Error: ENOENT: no such file or directory, open 'C:\\x\\nodebuild\\build.config.json'\n    at Object.readFileSync (node:fs:441:20)\n")]
    // gcc / clang: a missing header.
    [InlineData("src/main.c:3:10: fatal error: config.h: No such file or directory\ncompilation terminated.\n")]
    // rustc: include_str! of a missing file.
    [InlineData("error: couldn't read `src/data.txt`: No such file or directory (os error 2)\n --> src/main.rs:2:24\n")]
    // MSVC: C1083 — the same error in its own words.
    [InlineData("main.cpp(3): fatal error C1083: Cannot open include file: 'config.h': No such file or directory\n")]
    public void ARealBuildError_ThatMentionsAMissingFile_IsSaid(string output)
    {
        Assert.NotNull(SmartFixValidator.Interpret(1, output, dotnetFilter: false));
    }

    [Fact]
    public void AnOrdinaryCompileError_IsSaid_WithItsLine()
    {
        // Reference arm (go 1.23).
        var note = SmartFixValidator.Interpret(1, "# example.com/b\n./main.go:3:15: undefined: undefinedCall\n", dotnetFilter: false);

        Assert.NotNull(note);
        Assert.Contains("undefined: undefinedCall", note);
    }

    [Fact]
    public void UnderPowerShell_TheValidatorAsksForTheErrorViewThatNamesTheError()
    {
        // pwsh 7's default ConciseView prints the translated sentence and no id; NormalView prints both.
        Assert.StartsWith("$ErrorView = 'NormalView'", SmartFixValidator.ValidatorScript(ShellDialect.PowerShell, "go build ./..."));
        Assert.EndsWith("go build ./...", SmartFixValidator.ValidatorScript(ShellDialect.PowerShell, "go build ./..."));
        Assert.Equal("go build ./...", SmartFixValidator.ValidatorScript(ShellDialect.Posix, "go build ./..."));
    }
}
