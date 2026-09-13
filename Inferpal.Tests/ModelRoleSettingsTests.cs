using System.Text.Json;
using System.Text.Json.Nodes;
using Inferpal.Config;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Issue #8: "Use a separate model per role" unchecked promised the chat model everywhere and
/// only folded the role pickers away — the overrides stayed, the router used them, and the switch
/// came back checked.
/// </summary>
public class ModelRoleSettingsTests
{
    private static List<SettingField> RoleFields()
    {
        var fields = SettingsSchema.AllFields.Where(f => f.Gate == ModelRoleSettings.Gate).ToList();
        // Witness: agent, code actions, FIM, inline edit, utility, automatic routing.
        Assert.True(fields.Count >= 6, $"Only {fields.Count} field(s) behind the '{ModelRoleSettings.Gate}' gate — this test checks nothing.");
        return fields;
    }

    /// <summary>
    /// Driven by the schema, not by a list copied here: a role added behind the gate and forgotten by
    /// <see cref="ModelRoleSettings.UseChatModelEverywhere"/> fails this test.
    /// </summary>
    [Fact]
    public void Unchecked_ResetsEveryFieldBehindTheRolesGate()
    {
        var json = JsonSerializer.SerializeToNode(new InferpalConfig { DefaultModel = "chat-model" })!.AsObject();
        foreach (var field in RoleFields())
            json[field.Key] = field.Kind == SettingKind.Bool ? JsonValue.Create(true) : JsonValue.Create("role-model");
        var config = json.Deserialize<InferpalConfig>()!;
        Assert.True(ModelRoleSettings.HasRoleOverride(config));

        ModelRoleSettings.UseChatModelEverywhere(config);

        var after = JsonSerializer.SerializeToNode(config)!.AsObject();
        foreach (var field in RoleFields())
        {
            var node = after[field.Key];
            if (field.Kind == SettingKind.Bool)
                Assert.False(node?.GetValue<bool>() ?? false, $"{field.Key} is still on.");
            else
                Assert.True(string.IsNullOrEmpty(node?.GetValue<string>()), $"{field.Key} still names a model.");
        }
        Assert.False(ModelRoleSettings.HasRoleOverride(config));
        Assert.Equal("chat-model", config.DefaultModel);
    }

    [Fact]
    public void Unchecked_RoutesEveryRoleToTheChatModel()
    {
        // The reporter's state: an Ollama-style name left in the agent, utility and FIM fields while
        // connected to LM Studio, which has no such model.
        var config = new InferpalConfig
        {
            DefaultModel          = "qwen/qwen2.5-coder-14b",
            AgentModel            = "qwen3-coder:latest",
            UtilityModel          = "qwen3-coder:latest",
            InlineCompletionModel = "qwen3-coder:latest",
        };

        ModelRoleSettings.UseChatModelEverywhere(config);

        foreach (var role in Enum.GetValues<ModelRole>())
            Assert.Equal("qwen/qwen2.5-coder-14b", ModelRouter.Resolve(config, role));
    }

    [Fact]
    public void AFreshConfiguration_StartsWithTheSwitchUnchecked() =>
        Assert.False(ModelRoleSettings.HasRoleOverride(new InferpalConfig()));
}
