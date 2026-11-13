using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Localization;
using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio's settings mask a saved API key (mockups ApiKeyHidden, ApiKeyShown, ApiKeyChange): dots until asked
/// for, then read-only, and a new key typed in an empty box with the saved one a click away. Remote UI has no bindable
/// PasswordBox, so the mask is three boxes over one value.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class MaskedApiKeyTests
{
    private sealed class Form
    {
        public string? Saved = "sk-local-3f9c";
        public string  Box   = "sk-local-3f9c";
        public SecretFieldItem Item { get; }
        public Form(string? saved = "sk-local-3f9c")
        {
            Saved = saved;
            Box   = saved ?? string.Empty;
            Item  = new SecretFieldItem(() => Saved, () => Box, v => Box = v, a => { a(); return Task.CompletedTask; });
            Item.Reset();   // as the form does once every box holds its key
            Item.ApplyLabels("API key", "Only if your server asks for one.");
        }
    }

    /// <summary>A role's field hidden while its key is shown: masked, not still in clear when the field comes back.</summary>
    [Fact]
    public void AFieldHiddenWhileItsKeyIsShown_IsMaskedAgain()
    {
        var form = new Form();
        form.Item.Show();
        form.Item.Hidden();
        Assert.True(form.Item.IsMasked);
        Assert.False(form.Item.IsShown);
    }

    /// <summary>The buttons act on the form's own thread, like its other commands.</summary>
    [Fact]
    public async Task TheButtons_RunOnTheFormsThread()
    {
        var ran = 0;
        var item = new SecretFieldItem(() => "k", () => "k", _ => { }, a => { ran++; a(); return Task.CompletedTask; });
        item.Reset();
        foreach (var command in new[] { item.ShowCommand, item.HideCommand, item.ChangeCommand, item.KeepCommand })
            await command.ExecuteAsync(null, null!, CancellationToken.None);
        Assert.Equal(4, ran);
    }

    /// <summary>A Save ends while a new key is being typed: dots over it would hide a key that is not saved.</summary>
    [Fact]
    public void AKeyTypedWhileASaveRan_StaysInItsBox()
    {
        var form = new Form();
        form.Item.Change();
        form.Box = "sk-local-7b2e";
        form.Item.Reset();
        Assert.True(form.Item.IsEditing);
        Assert.True(form.Item.CanKeepSaved);

        // The box keeps what Save trims: still the saved key.
        form.Box = " sk-local-3f9c ";
        form.Item.Reset();
        Assert.True(form.Item.IsMasked);
    }

    /// <summary>A role moved back to the chat's server hides its key's field: a key being changed there goes back to the
    /// saved one, never erased by a Save that cannot show it.</summary>
    [Fact]
    public void AFieldHiddenWhileItsKeyIsChanged_KeepsTheSavedOne()
    {
        var form = new Form();
        form.Item.Change();
        form.Item.Hidden();
        Assert.Equal("sk-local-3f9c", form.Box);
        Assert.True(form.Item.IsMasked);

        // Reference arm: a field hidden while masked, or with no key saved, is left as it is.
        form.Item.Hidden();
        Assert.True(form.Item.IsMasked);
        var none = new Form(null);
        none.Box = "typed";
        none.Item.Hidden();
        Assert.Equal("typed", none.Box);
    }

    [Fact]
    public void ASavedKey_IsMasked_ShownOnlyWhenAsked_AndMaskedAgainWhenTheWindowCloses()
    {
        var form = new Form();
        Assert.True(form.Item.IsMasked);
        Assert.False(form.Item.IsEditing);
        Assert.Equal(Strings.HintKeySaved, form.Item.Hint);

        form.Item.Show();
        Assert.True(form.Item.IsShown);
        Assert.True(form.Item.CanChange);
        Assert.Equal(Strings.HintKeyShown, form.Item.Hint);
        Assert.Equal("sk-local-3f9c", form.Box);   // shown as it is, never edited by showing

        form.Item.MaskShown();
        Assert.True(form.Item.IsMasked);
        Assert.False(form.Item.IsShown);
    }

    [Fact]
    public void Change_EmptiesTheBox_AndTheSavedKeyComesBack_OnlyWhenAskedFor()
    {
        var form = new Form();
        form.Item.Change();
        Assert.Equal(string.Empty, form.Box);   // the saved key is never shown on the way to a new one
        Assert.True(form.Item.IsEditing);
        Assert.True(form.Item.CanKeepSaved);
        Assert.False(form.Item.CanChange);
        Assert.Equal(Strings.HintKeyReplacing, form.Item.Hint);

        // Closing the window keeps a key being typed.
        form.Box = "sk-local-7b2e";
        form.Item.MaskShown();
        Assert.True(form.Item.IsEditing);
        Assert.Equal("sk-local-7b2e", form.Box);

        form.Item.KeepSaved();
        Assert.Equal("sk-local-3f9c", form.Box);
        Assert.True(form.Item.IsMasked);
    }

    [Fact]
    public void ANewKeySaved_IsMaskedFromThenOn()
    {
        var form = new Form();
        form.Item.Change();
        form.Box = form.Saved = "sk-local-7b2e";   // what Save leaves behind
        form.Item.Reset();
        Assert.True(form.Item.IsMasked);
        Assert.Equal(Strings.HintKeySaved, form.Item.Hint);
    }

    /// <summary>Reference arm: no key saved, the box is the ordinary one, with the field's own hint.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoKeySaved_IsAnOrdinaryBox(string? saved)
    {
        var form = new Form(saved);
        Assert.True(form.Item.IsEditing);
        Assert.False(form.Item.IsMasked);
        Assert.False(form.Item.CanKeepSaved);
        Assert.False(form.Item.CanChange);
        Assert.Equal("Only if your server asks for one.", form.Item.Hint);
    }

    /// <summary>The property, not a list of fields: every box of the settings page that holds a key is the editable one
    /// only while a key is typed, or the read-only one only once asked for — a key field added later is held too.</summary>
    [Fact]
    public void EveryKeyBoxOfTheSettingsPage_IsMaskedUntilAskedFor()
    {
        var xaml = Regex.Replace(File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(),
                                                               "Inferpal", "ToolWindow", "InferpalSettingsContent.xaml")),
                                 "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        var boxes = Regex.Matches(xaml, @"<TextBox\b[^>]*Text=""\{Binding (\w*)ApiKey\b[^>]*>").ToList();
        Assert.True(boxes.Count >= 4, $"the page's key boxes were not found ({boxes.Count})");   // witness: under the six real ones
        foreach (var box in boxes)
        {
            // Each box follows its own key's mask: ApiKey the main one, CompletionApiKey and SearchApiKey their role's.
            var item = box.Groups[1].Value.Length == 0 ? "MainKey" : box.Groups[1].Value + "Key";
            if (box.Value.Contains("Mode=TwoWay", StringComparison.Ordinal))
                Assert.Contains($@"Visibility=""{{Binding {item}.IsEditing,", box.Value, StringComparison.Ordinal);
            else
            {
                Assert.Contains(@"IsReadOnly=""True""", box.Value, StringComparison.Ordinal);
                Assert.Contains($@"Visibility=""{{Binding {item}.IsShown,", box.Value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheForm_MasksOnLoadCancelAndSave_AndTheWindow_MasksAShownKeyAsItCloses()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var data = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));
        // Each mask reads its own key, saved as the unsaved count compares it, and its own box.
        Assert.Contains("MainKey = new(() => (string?)_opened[\"apiKey\"], () => ApiKey, v => ApiKey = v, RunOnVMContextAsync);",
                        Regex.Replace(data, @"\s+=\s+", " = "), StringComparison.Ordinal);
        Assert.Contains("CompletionKey = new(() => (string?)_opened[\"inlineCompletionApiKey\"], () => CompletionApiKey, v => CompletionApiKey = v, RunOnVMContextAsync);",
                        Regex.Replace(data, @"\s+=\s+", " = "), StringComparison.Ordinal);
        Assert.Contains("SearchKey = new(() => (string?)_opened[\"ragEmbeddingApiKey\"], () => SearchApiKey, v => SearchApiKey = v, RunOnVMContextAsync);",
                        Regex.Replace(data, @"\s+=\s+", " = "), StringComparison.Ordinal);
        Assert.Matches(@"InitRoleServers\(config\);\s*ResetKeys\(\);", data);
        // Save resets on the form's own thread: what follows its awaits runs on the thread pool.
        Assert.Matches(@"_opened = edited\.SnapshotNow\(\);\s*await RunOnVMContextAsync\(ResetKeys\);", data);
        var pages = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsData.Pages.cs"));
        Assert.Matches(@"LoadRoleServerFields\(c\);\s*ResetKeys\(\);", pages);
        var roles = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsData.RoleServers.cs"));
        Assert.Matches(@"if \(!CompletionElsewhere\) CompletionKey\.Hidden\(\);\s*if \(!SearchElsewhere\)\s*SearchKey\.Hidden\(\);", roles);
        var window = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "InferpalSettingsToolWindow.cs"));
        Assert.Matches(@"override Task OnHideAsync[\s\S]*?MaskShownKeys\(\)", window);
    }
}
