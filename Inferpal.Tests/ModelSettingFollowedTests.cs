using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: the window's model setting (<c>inferpal.model</c>) changed in the Settings editor or settings.json is
/// followed at once — the model button, every question, and the host for this session.
/// </summary>
/// <remarks>
/// It was read in one place, the host's start: changed afterwards, the button kept the old name and every question
/// went to the old model until the host restarted, while the Settings editor showed the new one.
/// </remarks>
public sealed class ModelSettingFollowedTests
{
    private static string Src(params string[] parts) =>
        Path.Combine([ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", .. parts]);

    [Fact]
    public void AChangeOfTheModelSetting_IsFollowed()
    {
        var src   = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Src("extension.ts")));
        var start = src.IndexOf("onDidChangeConfiguration(", StringComparison.Ordinal);
        Assert.True(start >= 0, "the configuration listener was not found: this test would have measured nothing.");
        var listener = src[start..src.IndexOf("}),", start, StringComparison.Ordinal)];

        Assert.Contains("affectsConfiguration('inferpal.utilityModel')", listener, StringComparison.Ordinal);   // witness
        Assert.Contains("affectsConfiguration('inferpal.model')", listener, StringComparison.Ordinal);
        Assert.Contains("chatView.followModelSetting()", listener, StringComparison.Ordinal);
    }

    [Fact]
    public void Following_UpdatesTheChat_TheButton_AndTheHostForThisSessionOnly()
    {
        var body = ConversationPersistenceSilenceTests.TsMethodBody(Src("chatViewProvider.ts"), "async followModelSetting(");

        Assert.Contains("get<string>('model'", body, StringComparison.Ordinal);
        Assert.Contains("this.model = configured", body, StringComparison.Ordinal);
        Assert.Contains("model: configured", body, StringComparison.Ordinal);                // the button
        Assert.Contains("host.modelsUseForSession(configured)", body, StringComparison.Ordinal);
        // A window's setting is not a pick: the shared default (the other editor's model) is never written.
        Assert.DoesNotContain("configUpdate", body, StringComparison.Ordinal);
        Assert.DoesNotContain("pushModelToHost", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWebview_RedrawsTheButton_WhenTheModelComesWithTheList()
    {
        var src   = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Src("webview", "main.ts")));
        var start = src.IndexOf("case 'models': {", StringComparison.Ordinal);
        Assert.True(start >= 0, "the models case was not found: this test would have measured nothing.");
        var branch = src[start..src.IndexOf("break;", start, StringComparison.Ordinal)];

        Assert.Contains("currentModel = msg.model", branch, StringComparison.Ordinal);
        Assert.Contains("renderModelButton()", branch, StringComparison.Ordinal);
    }
}
