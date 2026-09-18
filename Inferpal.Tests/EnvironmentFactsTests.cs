using Inferpal.Config;
using Inferpal.Services.Prompting;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The system prompt and <c>run_command</c>'s description asserted two facts the product knows to
/// be variable: the editor and the shell.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it cost.</b> <c>Strings.SystemPrompt</c> said "integrated in Visual Studio 2026 … run
/// PowerShell commands" — and <c>Inferpal.Host</c> serves the SAME resource to the VS Code
/// front-end. Every VS Code user therefore had a model convinced it lived in Visual Studio (it
/// answers with Solution Explorer and Rebuild Solution), and every Linux/macOS machine a model
/// asked for PowerShell against <c>/bin/bash</c> — on packages published for linux-x64 and
/// darwin-arm64 since 1.5.0.
/// </para>
/// <para>
/// <b>Why it was invisible.</b> The EXECUTION path had already been repaired: <see
/// cref="ShellLauncher"/> has resolved two dialects since §23, and <c>UserShellTool</c> carries the
/// same fault in a comment ("powershell.exe hard-coded here, so every user-defined tool died on the
/// published linux-x64/darwin-arm64 hosts"). What was left is the DESCRIPTION path — the one
/// nobody compiles.
/// </para>
/// </remarks>
[Collection(ShellSerialCollection.Name)]
public class EnvironmentFactsTests
{
    // ── The system prompt ────────────────────────────────────────────────────

    [Fact]
    public void Facts_NameTheEditorTheFrontEndDeclared()
    {
        var facts = new SystemPromptBuilder(new InferpalConfig(), "Visual Studio Code").EnvironmentFacts();
        Assert.Contains("Editor: Visual Studio Code.", facts);
    }

    [Fact]
    public void Facts_NameNoEditorAtAll_WhenTheFrontEndDeclaredNone()
    {
        // Declared, never inferred (the same rule as SignalScope.DeclareNoVsInProcessPeer): with no
        // declaration the line leaves the editor out, it does not guess one.
        var facts = new SystemPromptBuilder(new InferpalConfig()).EnvironmentFacts();
        Assert.DoesNotContain("Editor:", facts);
        Assert.Contains("Operating system:", facts);   // the rest of the facts still hold
    }

    [Fact]
    public void Facts_NameTheShellThisMachineActuallyRuns()
    {
        var previous = ShellLauncher._overrideForTests;
        try
        {
            ShellLauncher._overrideForTests = new ShellOverride(ShellDialect.Posix, "/bin/bash");
            var posix = new SystemPromptBuilder(new InferpalConfig(), "Visual Studio Code").EnvironmentFacts();
            Assert.Contains("The run_command shell is bash.", posix);

            ShellLauncher._overrideForTests = new ShellOverride(ShellDialect.PowerShell, "powershell.exe");
            var ps = new SystemPromptBuilder(new InferpalConfig(), "Visual Studio").EnvironmentFacts();
            Assert.Contains("The run_command shell is PowerShell.", ps);
        }
        finally { ShellLauncher._overrideForTests = previous; }
    }

    [Fact]
    public void TheFactsAreAppendedToTheBaseLayer_NotAddedAsANewSection()
    {
        // The /xray panel counts its layers by PromptSectionKind: one more section would be one
        // more line to translate into ten languages for two sentences of facts.
        var builder  = new SystemPromptBuilder(new InferpalConfig(), "Visual Studio");
        var sections = builder.BuildSections("BASE");

        Assert.Single(sections);
        Assert.StartsWith("BASE", sections[0].Content);
        Assert.Contains("Editor: Visual Studio.", sections[0].Content);
    }

    // ── run_command's description ────────────────────────────────────────────

    [Fact]
    public void RunCommand_DescribesTheDialectThisMachineRuns()
    {
        var previous = ShellLauncher._overrideForTests;
        try
        {
            using var tool = new RunCommandTool(new NoopApproval(), new InferpalConfig(), () => ".");

            ShellLauncher._overrideForTests = new ShellOverride(ShellDialect.Posix, "/bin/bash");
            Assert.Contains("Runs a bash command", tool.Description);
            Assert.Contains("export NAME=", tool.Description);
            Assert.DoesNotContain("PowerShell", tool.Description);
            Assert.Contains("bash command to execute", Describe(tool));

            ShellLauncher._overrideForTests = new ShellOverride(ShellDialect.PowerShell, "powershell.exe");
            Assert.Contains("Runs a PowerShell command", tool.Description);
            Assert.Contains("$env:NAME=", tool.Description);
            Assert.Contains("PowerShell command to execute", Describe(tool));
        }
        finally { ShellLauncher._overrideForTests = previous; }
    }

    // ── bash and sh are not the same language ────────────────────────────────

    [Fact]
    public void Facts_SayShWhenTheHostRunsSh_NotBash()
    {
        // §23 repaired "we ask a bash host for PowerShell". The opposite leg stayed: the POSIX
        // dialect was named "bash" outright, while ShellLauncher resolves /bin/sh on a busybox host
        // (Alpine, a common dev-container base). A model told "bash" writes [[ ]], arrays, `source`
        // and <<< — which ash refuses, one by one.
        var previous = ShellLauncher._overrideForTests;
        try
        {
            ShellLauncher._overrideForTests = new ShellOverride(ShellDialect.Posix, "/bin/sh");
            var facts = new SystemPromptBuilder(new InferpalConfig(), "Visual Studio Code").EnvironmentFacts();

            Assert.Contains("The run_command shell is sh.", facts);
            Assert.DoesNotContain("bash", facts);
        }
        finally { ShellLauncher._overrideForTests = previous; }
    }

    [Fact]
    public void RunCommand_DescribesShWhenTheHostRunsSh()
    {
        var previous = ShellLauncher._overrideForTests;
        try
        {
            using var tool = new RunCommandTool(new NoopApproval(), new InferpalConfig(), () => ".");

            ShellLauncher._overrideForTests = new ShellOverride(ShellDialect.Posix, "/bin/sh");
            Assert.Contains("Runs a sh command", tool.Description);
            Assert.Contains("sh command to execute", Describe(tool));
            Assert.DoesNotContain("bash", tool.Description);
        }
        finally { ShellLauncher._overrideForTests = previous; }
    }

    [Fact]
    public void TheSpokenNameIsTheLANGUAGE_NotTheFileName()
    {
        // Two reference arms in one test, because they say the same rule from both ends. pwsh and
        // powershell.exe speak THE SAME language: the name that matters to the model is the
        // language's, not the binary's. And on a Windows box driven at Git bash (PosixShellTests),
        // the executable is called bash.exe: the extension is not part of the name.
        //
        // ⚠ Git bash's path is composed with the PLATFORM's separator: hard-coded with backslashes,
        // it would measure, on CI's ubuntu/macOS legs, how .NET reads a foreign separator (it reads
        // none) instead of measuring that the extension is stripped.
        var gitBash = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Git", "bin", "bash.exe");

        Assert.Equal("PowerShell", ShellLauncher.SpokenName(ShellDialect.PowerShell, "/usr/bin/pwsh"));
        Assert.Equal("PowerShell", ShellLauncher.SpokenName(ShellDialect.PowerShell, "powershell.exe"));
        Assert.Equal("bash", ShellLauncher.SpokenName(ShellDialect.Posix, gitBash));
        Assert.Equal("bash", ShellLauncher.SpokenName(ShellDialect.Posix, "/bin/bash"));
        Assert.Equal("sh",   ShellLauncher.SpokenName(ShellDialect.Posix, "/bin/sh"));
    }

    /// <summary>The parameter schema as the model receives it — this is the half that said
    /// "PowerShell command to execute" whatever the shell was.</summary>
    private static string Describe(RunCommandTool tool) =>
        System.Text.Json.JsonSerializer.Serialize(tool.Parameters);
}
