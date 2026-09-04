using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A numeric settings box has <b>three</b> states, not two: readable, cleared, and unreadable. The
/// last two were conflated, and the confusion cost the user a value.
/// </summary>
/// <remarks>
/// Measured in <c>InferpalSettingsData</c>: the nine unsanitized numeric boxes read
/// <c>int.TryParse(text, out var v) ? clamp(v) : &lt;factory default&gt;</c>. A typo — <c>4o</c>
/// for <c>40</c> — therefore reset that setting to its factory value, the save announced
/// "saved", and the box kept showing the faulty input.
///
/// ⚠ The <b>h/min/s</b> boxes were never affected: their setter sanitizes as you type
/// (<c>DurationFields.Clamp</c> keeps digits only), so nothing unreadable reaches the save. The
/// measurement is what bounded the defect — without it, "fixing all thirteen numeric boxes" would
/// have touched four healthy sites.
/// </remarks>
public class SettingsFallbackTests
{
    [Fact]
    public void AClearedBox_RestoresTheDefault()
    {
        // Clearing the box IS the existing affordance for "give me the default back": preserved.
        Assert.Equal(20, SettingsFallback.For(null, current: 40, whenCleared: 20));
        Assert.Equal(20, SettingsFallback.For("", current: 40, whenCleared: 20));
        Assert.Equal(20, SettingsFallback.For("   ", current: 40, whenCleared: 20));
        Assert.Equal(20, SettingsFallback.For("\t\r\n", current: 40, whenCleared: 20));
    }

    [Fact]
    public void ATypo_KeepsWhatIsConfigured_NeverTheFactoryDefault()
    {
        // The heart of the fix: "4o" must not mean "put 20 back".
        Assert.Equal(40, SettingsFallback.For("4o", current: 40, whenCleared: 20));
        Assert.Equal(40, SettingsFallback.For("forty", current: 40, whenCleared: 20));
        Assert.Equal(40, SettingsFallback.For("-", current: 40, whenCleared: 20));
        // A comma where the culture expects a dot: the most ordinary mistake of the two decimal
        // fields (VramBudgetGb, RagSimilarityThreshold).
        Assert.Equal(0.35f, SettingsFallback.For("0,35", current: 0.35f, whenCleared: 0.20f));
    }

    [Fact]
    public void ItCarriesNoOpinionOnTheValueItself()
    {
        // The function does not parse: it only chooses WHAT TO KEEP when parsing failed. Clamping
        // and culture stay at the call site, where they differ from one field to the next
        // (CurrentCulture for VRAM, InvariantCulture for the RAG threshold).
        Assert.Equal(0, SettingsFallback.For("x", current: 0, whenCleared: 7));
        Assert.Equal(-3, SettingsFallback.For("x", current: -3, whenCleared: 7));
        Assert.Equal("kept", SettingsFallback.For("x", current: "kept", whenCleared: "factory"));
    }

    /// <summary>
    /// What is <b>named</b> to the user: what they typed and which did not take. Never an empty
    /// box, whose effect is intended.
    /// </summary>
    /// <remarks>
    /// Arbitration: we save and we name, rather than refusing the whole form — one faulty input
    /// must not cancel the other valid edits of the same save.
    /// </remarks>
    [Fact]
    public void OnlyWhatWasTypedAndNotAppliedIsNamed()
    {
        // Typed, not kept: named.
        Assert.True(SettingsFallback.WasIgnored("4o", applied: false));
        Assert.True(SettingsFallback.WasIgnored("0", applied: false));   // parsed, but refused by the site

        // Kept: nothing to say, however exotic the text.
        Assert.False(SettingsFallback.WasIgnored("40", applied: true));

        // Cleared on purpose: that is the "give me the default back" affordance, not a lost input.
        Assert.False(SettingsFallback.WasIgnored(null, applied: false));
        Assert.False(SettingsFallback.WasIgnored("", applied: false));
        Assert.False(SettingsFallback.WasIgnored("   ", applied: false));
        Assert.False(SettingsFallback.WasIgnored("\t\r\n", applied: false));
    }

    /// <summary>A label quoted inside a sentence drops its colon, in every language.</summary>
    [Fact]
    public void ALabelQuotedInASentenceDropsItsColon()
    {
        Assert.Equal("Results per query (top-K)", SettingsFallback.LabelForSentence("Results per query (top-K):"));
        Assert.Equal("Fenêtre de contexte", SettingsFallback.LabelForSentence("Fenêtre de contexte :"));
        Assert.Equal("Seuil", SettingsFallback.LabelForSentence("Seuil :"));
        Assert.Equal("コンテキスト", SettingsFallback.LabelForSentence("コンテキスト："));

        // Labels that carry none stay untouched — four of the nine boxes are in that case.
        Assert.Equal("Max iterations", SettingsFallback.LabelForSentence("Max iterations"));
        Assert.Equal("VRAM budget", SettingsFallback.LabelForSentence("VRAM budget"));

        // And nothing else is trimmed: a label is not a sentence to punctuate.
        Assert.Equal("Unload after (minutes)", SettingsFallback.LabelForSentence("Unload after (minutes):"));
    }
}
