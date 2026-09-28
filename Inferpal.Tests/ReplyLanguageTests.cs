using System.Globalization;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The model-facing prompts are English only; the interface language reaches the model as one line.
//
//  Translated, the system prompt was ten specifications of which only the English one was ever
//  measured, and they had drifted (the French one: "reply in French unless the project convention
//  requires English"). Replayed in French, German, Japanese and Chinese on two models, an English
//  system prompt with this line kept every reply in the question's language.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]
public sealed class ReplyLanguageTests
{
    [Theory]
    [InlineData("fr-FR", "French")]
    [InlineData("de", "German")]
    [InlineData("ja", "Japanese")]
    [InlineData("zh-CN", "Chinese")]
    public void ANonEnglishInterface_NamesTheReplyLanguage(string culture, string name)
    {
        Assert.Equal($" The user's interface language is {name}: reply in {name} unless the user writes in another language.",
                     SystemPromptBuilder.ReplyLanguage(CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void AnEnglishOrInvariantInterface_AddsNothing()
    {
        // Reference arm: the base prompt's "respond in the same language as the user" is the whole rule.
        Assert.Equal(string.Empty, SystemPromptBuilder.ReplyLanguage(CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal(string.Empty, SystemPromptBuilder.ReplyLanguage(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheSystemPrompt_IsTheSameText_InEveryInterfaceLanguage_PlusTheLanguageLine()
    {
        var previous = Strings.OverrideCulture?.Name;
        try
        {
            Strings.ApplyLanguage("fr");
            var french = new SystemPromptBuilder(new InferpalConfig()).Build(ModelPrompts.SystemPrompt);
            Strings.ApplyLanguage("de");
            var german = new SystemPromptBuilder(new InferpalConfig()).Build(ModelPrompts.SystemPrompt);

            Assert.StartsWith(ModelPrompts.SystemPrompt, french, StringComparison.Ordinal);
            Assert.StartsWith(ModelPrompts.SystemPrompt, german, StringComparison.Ordinal);
            Assert.Contains("reply in French unless", french, StringComparison.Ordinal);
            Assert.Contains("reply in German unless", german, StringComparison.Ordinal);
        }
        finally { Strings.ApplyLanguage(previous); }
    }
}
