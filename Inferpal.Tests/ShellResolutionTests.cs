using Inferpal.Services.Shell;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>ShellLauncher</c> looked <c>pwsh</c> up ON THE PATH and wrote <c>/bin/bash</c> DOWN: the same
/// question asked two ways.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it cost.</b> A host whose bash is not at <c>/bin/bash</c> (NixOS puts it in
/// <c>/nix/store</c> and ships only <c>/bin/sh</c>) or that has none at all (Alpine/busybox, one of
/// the commonest dev-container bases, and the linux-x64 VSIX has shipped since 1.5.0) was handed a
/// <c>FileName</c> that does not exist. <c>Process.Start</c> then throws, so every
/// <c>run_command</c>, the persistent shell, every background job and every user shell tool died on
/// a <c>Win32Exception</c> — while the POSIX wrapper of
/// <see cref="ShellStateProtocol"/>, which uses nothing but <c>printf</c>/<c>eval</c>/<c>base64</c>/
/// <c>awk</c>/<c>printenv</c>/<c>tr</c> (no bashism: no array, no <c>local</c>, no
/// <c>[[ ]]</c>), would have run unchanged under <c>/bin/sh</c>.
/// </para>
/// <para>
/// <b>The reference arm is the essential part</b>: on every machine where the product works today,
/// bash is on the PATH, so resolution returns the same interpreter as before. This fix only adds the
/// paths where it used to crash.
/// </para>
/// </remarks>
public class ShellResolutionTests
{
    /// <summary>A fake PATH: the table says what the simulated machine has.</summary>
    private static Func<string, string?> Path(params string[] found) =>
        name => found.FirstOrDefault(f => System.IO.Path.GetFileName(f) == name);

    private static Func<string, bool> Files(params string[] present) =>
        p => present.Contains(p, StringComparer.Ordinal);

    // ── The reference arms: what works today must work the same ────────────────

    [Fact]
    public void Pwsh_OnThePath_StillWins()
    {
        // Full PowerShell semantics: nothing else changes. Unchanged since §23.
        var (dialect, file) = ShellLauncher.ResolvePosixHost(
            Path("/usr/bin/pwsh", "/bin/bash"), Files("/usr/bin/pwsh", "/bin/bash"));

        Assert.Equal(ShellDialect.PowerShell, dialect);
        Assert.Equal("/usr/bin/pwsh", file);
    }

    [Fact]
    public void OnAnOrdinaryLinuxHost_TheShellIsStillABash()
    {
        var (dialect, file) = ShellLauncher.ResolvePosixHost(Path("/bin/bash"), Files("/bin/bash"));

        Assert.Equal(ShellDialect.Posix, dialect);
        Assert.Equal("bash", System.IO.Path.GetFileNameWithoutExtension(file));
    }

    // ── The hosts where we returned an executable that does not exist ─────────

    [Fact]
    public void WhenBashLivesElsewhere_ItIsFoundWhereThePathSaysItIs()
    {
        // NixOS: /bin holds only sh, bash lives in /nix/store and is on the PATH.
        var (dialect, file) = ShellLauncher.ResolvePosixHost(
            Path("/nix/store/ab12/bin/bash", "/bin/sh"), Files("/nix/store/ab12/bin/bash", "/bin/sh"));

        Assert.Equal(ShellDialect.Posix, dialect);
        Assert.Equal("/nix/store/ab12/bin/bash", file);
    }

    [Fact]
    public void WhenTheHostHasNoBashAtAll_ItFallsBackToSh()
    {
        // Alpine/busybox: /bin/sh is ash, and the POSIX wrapper uses nothing ash refuses.
        var (dialect, file) = ShellLauncher.ResolvePosixHost(Path("/bin/sh"), Files("/bin/sh"));

        Assert.Equal(ShellDialect.Posix, dialect);
        Assert.Equal("/bin/sh", file);
    }

    [Fact]
    public void WhenNoProbeFindsAnything_TheLastResortIsTheOnePosixGuarantees()
    {
        // Nothing on the PATH, nothing under /bin: something has to be returned, and /bin/sh is the
        // only interpreter POSIX requires to exist. Returning a bash we KNOW is absent, no.
        var (dialect, file) = ShellLauncher.ResolvePosixHost(Path(), Files());

        Assert.Equal(ShellDialect.Posix, dialect);
        Assert.Equal("/bin/sh", file);
    }

    // ── The witness: resolution never returns a path its probes denied ────────

    [Theory]
    [InlineData("/bin/bash")]
    [InlineData("/nix/store/ab12/bin/bash")]
    [InlineData("/bin/sh")]
    public void WhateverItResolves_ThatFileWasSeenBySomeProbe(string only)
    {
        // Fixture witness: without it, a fix that always returned "/bin/sh" would pass the four
        // cases above, where /bin/sh happens to be present. Here one single thing exists on the
        // simulated machine, and that is what must come out.
        var (_, file) = ShellLauncher.ResolvePosixHost(Path(only), Files(only));
        Assert.Equal(only, file);
    }
}
