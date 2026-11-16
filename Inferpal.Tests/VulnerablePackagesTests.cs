using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// NuGet Audit's warnings (<c>NU1901</c>-<c>NU1904</c>) were one warning among the others, with no way out named: the
/// model now reads them above the report, by severity, with the command that fixes them, never as an error.
/// </summary>
public sealed class VulnerablePackagesTests
{
    // Verbatim from `dotnet build --no-restore` on a project referencing Newtonsoft.Json 12.0.1 (16/11, SDK 11.0.100).
    private const string Real =
        @"C:\src\Vuln\Vuln.csproj : warning NU1903: Package 'Newtonsoft.Json' 12.0.1 has a known high severity vulnerability, https://github.com/advisories/GHSA-5crp-9r3c-p9vr";

    [Fact]
    public void AFlaggedPackage_IsNamed_WithItsSeverity_AdvisoryAndTheFix()
    {
        var note = GetDiagnosticsTool.VulnerablePackages(Real + "\n" + Real);   // the build writes it twice: said once

        Assert.NotNull(note);
        Assert.Single(note!.Split('\n'), l => l.StartsWith("- Newtonsoft.Json 12.0.1 — high — https://github.com/advisories/GHSA-5crp-9r3c-p9vr", StringComparison.Ordinal));
        Assert.Contains("dotnet package update --vulnerable", note);
    }

    [Fact]
    public void ACleanBuild_SaysNothing() =>
        Assert.Null(GetDiagnosticsTool.VulnerablePackages("Build succeeded.\n    0 Warning(s)\n    0 Error(s)"));

    [Fact]
    public void AnUnreachableAudit_IsSaid_NotTakenForClean() =>
        Assert.Contains("could not reach", GetDiagnosticsTool.VulnerablePackages(
            "X.csproj : warning NU1900: Error occurred while getting package vulnerability data")!);

    [Fact]
    public void TheReport_KeepsItsVerdictFirst_AndTheSectionBelow()
    {
        var run = new ChildProcessResult(0, Real + "\nBuild succeeded.", string.Empty, TimedOut: false);

        var report = GetDiagnosticsTool.Interpret(run, "Vuln.csproj", 120);

        Assert.Contains("Vulnerable packages", report);
        Assert.Equal(GetDiagnosticsTool.BuildVerdict.Clean, GetDiagnosticsTool.ReadVerdict(report));   // a green build stays green
    }

    [Fact]
    public void OnePackage_SeveralAdvisories_IsOneLine_AtItsWorstSeverity_WithEveryAdvisory()
    {
        var note = GetDiagnosticsTool.VulnerablePackages(
            "a.csproj : warning NU1902: Package 'System.Text.Json' 8.0.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-aaaa\n" +
            "a.csproj : warning NU1903: Package 'System.Text.Json' 8.0.0 has a known high severity vulnerability, https://github.com/advisories/GHSA-bbbb\n" +
            "a.csproj : warning NU1900: Error occurred while getting package vulnerability data")!;

        Assert.Contains("- System.Text.Json 8.0.0 — high — https://github.com/advisories/GHSA-aaaa, https://github.com/advisories/GHSA-bbbb", note);
        Assert.Contains("NU1900", note);   // another source unchecked, said beside the findings
    }
}
