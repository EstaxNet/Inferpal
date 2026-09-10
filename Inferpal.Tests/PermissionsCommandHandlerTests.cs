using Inferpal.Localization;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/permissions</c> exists because <c>.inferpal/permissions.json</c> was, of the four committable
/// governance artifacts, the <b>only one with no listing surface</b> — and the only one that
/// <b>restricts</b> rather than advises. It can stop applying every one of its rules in silence, and
/// the only channel that said so was <c>/diagnostics</c>, which the 2026-09-06 arbitration
/// established is not enough: nobody opens it after writing a rule they believe they have set.
/// </summary>
public class PermissionsCommandHandlerTests
{
    private static string Run(string? json, string? configRules = null) =>
        PermissionsCommandHandler.Permissions("C:/ws", configRules, _ => json);

    // ── The state that brought the command into being ───────────────────────

    [Fact]
    public void BrokenJson_SaysNoneOfTheRestrictionsAreInForce()
    {
        var text = Run("{ this is not json");

        Assert.Contains(Strings.PermissionsOverlayUnusable, text);
    }

    [Fact]
    public void NoRulesArray_SaysTheSameThing()
    {
        // The file is perfectly valid JSON — and restricts nothing. Two causes, one consequence,
        // and it is the consequence that matters to the reader.
        var text = Run("{ \"regles\": [\"deny run_command .\"] }");

        Assert.Contains(Strings.PermissionsOverlayUnusable, text);
    }

    [Fact]
    public void MalformedEntry_IsCountedAndNamedAsNotInForce()
    {
        var text = Run("{ \"rules\": [\"deny run_command rm\", \"this is not a rule\"] }");

        Assert.Contains(Strings.PermissionsOverlayMalformed(1), text);
        Assert.Contains("deny run_command rm", text);   // the one that holds is still there
    }

    [Fact]
    public void AllowRule_IsReportedApart_BecauseItIsTheContractNotADefect()
    {
        // ⚠ Two distinct outcomes on purpose: a malformed line is a defect to fix, a discarded
        // `allow` rule is the overlay's contract (deny-only). Conflating them would send the user
        // off to repair correct syntax.
        var text = Run("{ \"rules\": [\"allow write_file .\"] }");

        Assert.Contains(Strings.PermissionsOverlayAllowIgnored(1), text);
        Assert.DoesNotContain(Strings.PermissionsOverlayMalformed(1), text);
    }

    // ── Witnesses: the healthy states do not shout ──────────────────────────

    [Fact]
    public void AHealthyOverlay_ListsItsRulesAndWarnsAboutNothing()
    {
        var text = Run("{ \"rules\": [\"deny run_command rm -rf\", \"deny write_file secrets\"] }");

        Assert.Contains("deny run_command rm -rf", text);
        Assert.Contains("deny write_file secrets", text);
        Assert.DoesNotContain(Strings.PermissionsOverlayUnusable, text);
        Assert.DoesNotContain(Strings.PermissionsOverlayMalformed(1), text);
        Assert.DoesNotContain(Strings.PermissionsOverlayEmpty, text);
    }

    [Fact]
    public void AnEmptyRulesArray_IsLegitimate_NotUnusable()
    {
        // WITNESS: without it, a check treating "zero rules" as "unreadable" would pass the four
        // tests above while accusing a correct file.
        var text = Run("{ \"rules\": [] }");

        Assert.Contains(Strings.PermissionsOverlayEmpty, text);
        Assert.DoesNotContain(Strings.PermissionsOverlayUnusable, text);
    }

    [Fact]
    public void NoOverlayFile_SaysSo_AndIsNotAFailure()
    {
        var text = Run(null);

        Assert.Contains(Strings.PermissionsOverlayAbsent, text);
        Assert.DoesNotContain(Strings.PermissionsOverlayUnusable, text);
    }

    [Fact]
    public void NoWorkspace_IsItsOwnAnswer()
    {
        var text = PermissionsCommandHandler.Permissions(null, null, _ => null);

        Assert.Contains(Strings.PermissionsNoWorkspace, text);
    }

    // ── The per-machine half, and the ORDER of evaluation ───────────────────

    [Fact]
    public void MachineRules_AreListedAfterTheOverlay_BecauseThatIsHowTheyAreEvaluated()
    {
        // The overlay is composed BEFORE the config (PermissionPolicy.Compose): a report showing
        // them the other way round would be right about the content and wrong about the effect.
        var text = Run("{ \"rules\": [\"deny run_command OVERLAYRULE\"] }",
                       configRules: "allow read_file MACHINERULE");

        var overlayAt = text.IndexOf("OVERLAYRULE", StringComparison.Ordinal);
        var machineAt = text.IndexOf("MACHINERULE", StringComparison.Ordinal);

        Assert.True(overlayAt >= 0 && machineAt >= 0, text);
        Assert.True(overlayAt < machineAt, "The overlay must precede the per-machine config.");
    }

    [Fact]
    public void AMalformedMachineLine_IsCountedToo()
    {
        var text = Run(null, configRules: "allow read_file .\nthis is not a rule");

        Assert.Contains(Strings.PermissionsConfigDropped(1), text);
    }

    [Fact]
    public void TheBuiltInDenylist_IsAlwaysMentioned()
    {
        // It applies whatever the rules above say: a listing that did not name it would suggest
        // that what it shows is everything that decides.
        Assert.Contains(Strings.PermissionsDenylistNote, Run(null));
    }
}
