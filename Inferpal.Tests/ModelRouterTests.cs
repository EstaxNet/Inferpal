using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

// Model Router V1: central task→model resolution. Locks the fallback chains that used to be
// duplicated across the VS commands and the chat VM.
public class ModelRouterTests
{
    private static InferpalConfig Config() => new() { DefaultModel = "chat-model" };

    [Fact]
    public void EveryRole_FallsBackToDefaultModel_WhenNothingConfigured()
    {
        // ModelRole is internal, so a [Theory] can't take it as a public parameter — loop instead.
        foreach (ModelRole role in System.Enum.GetValues(typeof(ModelRole)))
            Assert.Equal("chat-model", ModelRouter.Resolve(Config(), role));
    }

    [Fact]
    public void Agent_UsesAgentModel_WhenSet()
    {
        var cfg = Config();
        cfg.AgentModel = "agent-model";
        Assert.Equal("agent-model", ModelRouter.Resolve(cfg, ModelRole.Agent));
        Assert.Equal("chat-model", ModelRouter.Resolve(cfg, ModelRole.Chat)); // chat unaffected
    }

    [Fact]
    public void Utility_UsesUtilityModel_WhenSet()
    {
        var cfg = Config();
        cfg.UtilityModel = "small-model";
        Assert.Equal("small-model", ModelRouter.Resolve(cfg, ModelRole.Utility));
    }

    [Fact]
    public void CodeActions_UsesCodeActionsModel_WhenSet()
    {
        var cfg = Config();
        cfg.CodeActionsModel = "ca-model";
        Assert.Equal("ca-model", ModelRouter.Resolve(cfg, ModelRole.CodeActions));
    }

    [Fact]
    public void InlineEdit_ChainsThroughCodeActions_ThenDefault()
    {
        var cfg = Config();
        Assert.Equal("chat-model", ModelRouter.Resolve(cfg, ModelRole.InlineEdit));

        cfg.CodeActionsModel = "ca-model";
        Assert.Equal("ca-model", ModelRouter.Resolve(cfg, ModelRole.InlineEdit));

        cfg.InlineEditModel = "ie-model";
        Assert.Equal("ie-model", ModelRouter.Resolve(cfg, ModelRole.InlineEdit));
    }

    [Fact]
    public void Fim_UsesInlineCompletionModel_WhenSet()
    {
        var cfg = Config();
        cfg.InlineCompletionModel = "fim-model";
        Assert.Equal("fim-model", ModelRouter.Resolve(cfg, ModelRole.Fim));
    }

    [Fact]
    public void WhitespaceOverride_IsTreatedAsUnset_AndResultIsTrimmed()
    {
        var cfg = Config();
        cfg.UtilityModel = "   ";
        Assert.Equal("chat-model", ModelRouter.Resolve(cfg, ModelRole.Utility));

        cfg.UtilityModel = " small-model ";
        Assert.Equal("small-model", ModelRouter.Resolve(cfg, ModelRole.Utility));
    }
}
