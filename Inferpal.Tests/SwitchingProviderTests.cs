using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The provider Visual Studio holds for its whole life: every member reaches the inside, and the inside is rebuilt only
/// for another kind of server.
/// </summary>
public sealed class SwitchingProviderTests
{
    /// <summary>An inside that records each member it is asked for and answers a default.</summary>
    public class Recorder : DispatchProxy
    {
        public readonly List<string> Asked = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Asked.Add(method!.Name);
            var type = method.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }

    private static (SwitchingProvider Wrapper, Recorder Inside) Build()
    {
        var inside = DispatchProxy.Create<IInferenceProvider, Recorder>();
        return (new SwitchingProvider(new InferpalConfig(), _ => inside), (Recorder)(object)inside);
    }

    /// <summary>Every member of the interface, its default-bodied ones and the reasoning-effort overload included, reaches
    /// the inside: left to their default body they would answer without it.</summary>
    [Fact]
    public void EveryMember_ReachesTheInside()
    {
        var (wrapper, inside) = Build();
        var members = typeof(IInferenceProvider).GetMethods().Concat(typeof(IChatTurnClient).GetMethods()).ToList();
        Assert.True(members.Count >= 25, $"the interface reads as {members.Count} members");   // the witness

        foreach (var member in members)
        {
            inside.Asked.Clear();
            var args = member.GetParameters().Select(p =>
                p.HasDefaultValue ? p.DefaultValue
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
            member.Invoke(wrapper, args);
            Assert.True(inside.Asked.Contains(member.Name), $"{member} answered without reaching the inside");
        }
    }

    /// <summary>The inside is rebuilt for another kind of server only; the same kind keeps it (its breakers and caches).</summary>
    [Fact]
    public void TheInside_IsRebuilt_ForAnotherKindOnly()
    {
        var built = 0;
        var config = new InferpalConfig { Provider = "ollama" };
        var wrapper = new SwitchingProvider(config, _ => { built++; return new FakeInferenceProvider(); });

        Assert.False(wrapper.Follow());                    // nothing moved
        config.BaseUrl = "http://elsewhere:11434";
        Assert.True(wrapper.Follow());                     // the server moved: said, for the caller to check it
        Assert.Equal(1, built);                            // same kind: the inside is kept
        config.Language = "fr";
        Assert.False(wrapper.Follow());                    // another setting: taken, the server did not move

        config.Provider = "lmstudio";
        Assert.Equal(InferenceProviderFactory.LmStudio, InferenceProviderFactory.PendingSwitch(wrapper, config.Provider));
        Assert.True(wrapper.Follow());
        Assert.Equal(2, built);
        Assert.Equal(InferenceProviderFactory.LmStudio, InferenceProviderFactory.CodeOf(wrapper));
        Assert.Null(InferenceProviderFactory.PendingSwitch(wrapper, config.Provider));
    }

    /// <summary>A new kind of server another editor saved is TAKEN by Visual Studio's client, which rebuilds itself at the
    /// door; a client built once (the host's, ACP's) leaves the server aside and says it (reference arm).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANewKindInTheFile_IsTaken_OnlyByAClientThatRebuildsItself(bool switching)
    {
        var config = new InferpalConfig { Provider = InferenceProviderFactory.Ollama };
        IInferenceProvider client = switching
            ? new SwitchingProvider(config, _ => new FakeInferenceProvider())
            : InferenceProviderFactory.Create(config);
        var followed = config.SnapshotNow();
        var fileNow = new InferpalConfig { Provider = InferenceProviderFactory.LmStudio };

        var result = SettingsFollow.Take(config, fileNow, client, ref followed);

        Assert.Equal(switching ? InferenceProviderFactory.LmStudio : InferenceProviderFactory.Ollama, config.Provider);
        Assert.Equal(!switching, result.ServerChanged);
    }

    /// <summary>The inside reads its own copy of the settings: an address saved under a turn reaches it at the door only,
    /// so the turn keeps the server it started with. Reference arm: after the door, the copy has it.</summary>
    [Fact]
    public void AnAddressChangedUnderATurn_ReachesTheInside_AtTheDoorOnly()
    {
        InferpalConfig? seen = null;
        var config = new InferpalConfig { BaseUrl = "http://old:11434" };
        var wrapper = new SwitchingProvider(config, c => { seen = c; return new FakeInferenceProvider(); });

        config.BaseUrl = "http://new:11434";
        Assert.Equal("http://old:11434", seen!.BaseUrl);

        wrapper.Follow();
        Assert.Equal("http://new:11434", seen.BaseUrl);
    }
}
