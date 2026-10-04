using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inferpal.Config;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The "Show advanced settings" box of the Server and models page only folds. What it must never do
/// is hide a setting in effect: issue #8 was per-role models the router kept using behind a folded
/// switch — a name the backend did not have, a 404 on every agent turn, and nothing on screen.
/// </summary>
public class ModelRoleSettingsTests
{
    /// <summary>The per-task models and the routing switch folded under the box, read from the schema.</summary>
    private static List<SettingField> FoldedRoleFields()
    {
        var fields = SettingsSchema.Tabs.SelectMany(t => t.Sections)
            .Where(s => s.Gate == ModelRoleSettings.Gate)
            .SelectMany(s => s.Fields)
            .Where(f => f.Kind == SettingKind.Model || f.Key == "modelRouterAuto")
            .ToList();
        // Witness: agent, code actions, inline edit, utility, automatic routing.
        Assert.True(fields.Count >= 5, $"Only {fields.Count} role field(s) behind the '{ModelRoleSettings.Gate}' gate — this test checks nothing.");
        return fields;
    }

    /// <summary>
    /// Driven by the schema, not by a list copied here: a per-task model added behind the fold and
    /// forgotten by <see cref="ModelRoleSettings.OpensAdvanced"/> fails this test.
    /// </summary>
    [Fact]
    public void EveryFoldedRole_InEffect_OpensThePageWithTheFoldShown()
    {
        foreach (var field in FoldedRoleFields())
        {
            var json = JsonSerializer.SerializeToNode(new InferpalConfig { DefaultModel = "chat-model" })!.AsObject();
            json[field.Key] = field.Kind == SettingKind.Bool ? JsonValue.Create(true) : JsonValue.Create("role-model");
            var config = json.Deserialize<InferpalConfig>()!;

            Assert.True(ModelRoleSettings.OpensAdvanced(config), $"{field.Key} is in effect behind a closed fold.");
        }
    }

    [Fact]
    public void TheModelMakersSamplingTurnedOff_OpensThePageWithTheFoldShown() =>
        Assert.True(ModelRoleSettings.OpensAdvanced(new InferpalConfig { UseRecommendedSampling = false }));

    /// <summary>The autocomplete model is shown outside the fold: setting it is no reason to open it.</summary>
    [Fact]
    public void TheAutocompleteModel_IsNotAReasonToOpenTheFold() =>
        Assert.False(ModelRoleSettings.OpensAdvanced(new InferpalConfig { InlineCompletionModel = "mellum2" }));

    [Fact]
    public void AFreshConfiguration_OpensWithTheFoldClosed() =>
        Assert.False(ModelRoleSettings.OpensAdvanced(new InferpalConfig()));

    /// <summary>
    /// The box folds, it never writes: the Visual Studio window's save no longer clears the per-task
    /// models behind it. A source assertion — the save runs inside the Remote UI view model.
    /// </summary>
    [Fact]
    public void TheVisualStudioWindow_SavesTheFoldedSettings_WhateverTheBoxSays()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(dir!.FullName, "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));

        Assert.Contains("edited.AgentModel", code, StringComparison.Ordinal);             // WITNESS: the save is read
        Assert.DoesNotContain("UseChatModelEverywhere", code, StringComparison.Ordinal);
        Assert.DoesNotContain("if (separateRoleModels)", code, StringComparison.Ordinal);
    }
}
