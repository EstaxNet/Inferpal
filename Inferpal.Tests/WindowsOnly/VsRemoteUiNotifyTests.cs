using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Inferpal.Config;
using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Visual Studio reads a Remote UI view model's <c>[DataMember]</c>s once, when the window takes it, and then only what a
/// PropertyChanged tells it. The labels and commands are set by <c>ApplyLabels</c>, posted AFTER that first read: a member
/// it sets without raising PropertyChanged never reaches the screen — a panel without a word, a button with no command
/// (the tools assistant's 25 labels; "Turn approvals off", which then did nothing). Measured at the screen, invisible to
/// every rendering that reads the initial values.
/// </summary>
public sealed class VsRemoteUiNotifyTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string _path = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"remote-ui-notify-{Guid.NewGuid():N}.json");

    public VsRemoteUiNotifyTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { File.Delete(_path); } catch { /* best-effort cleanup */ }
    }

    private static Dictionary<string, object?> Members(object vm) =>
        vm.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
          .Where(p => p.GetCustomAttribute<DataMemberAttribute>() is not null && p.GetIndexParameters().Length == 0)
          .ToDictionary(p => p.Name, p => p.GetValue(vm));

    [Fact]
    public async Task EverySettingsMemberApplyLabelsChanges_TellsVisualStudio()
    {
        new InferpalConfig { DefaultModel = "m" }.Save();
        var config = InferpalConfig.Load();
        var client = new FakeInferenceProvider();
        await using var mcp = new Services.Mcp.McpToolService(config, new NoopApproval());
        var form = new InferpalSettingsData(config, client, null!, mcp);
        // Listening BEFORE the snapshot: a background refresh between the two would read as a silent change.
        var told = new HashSet<string>(StringComparer.Ordinal);
        ((INotifyPropertyChanged)form).PropertyChanged += (_, e) => { lock (told) told.Add(e.PropertyName ?? ""); };
        var before = Members(form);

        form.ApplyLabels();

        var after = Members(form);
        var changed = after.Where(kv => !Equals(before[kv.Key], kv.Value)).Select(kv => kv.Key).ToList();
        Assert.True(changed.Count >= 50, $"witness: ApplyLabels changed only {changed.Count} members — the reading is not alive");
        var silent = changed.Where(n => !told.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(silent.Count == 0, "set by ApplyLabels without telling Visual Studio: " + string.Join(", ", silent));
    }

    /// <summary>The chat window, the same way.</summary>
    [Fact]
    public void EveryChatMemberApplyLabelsChanges_TellsVisualStudio()
    {
        using var vs = new VsWindowHarness();
        var told = new HashSet<string>(StringComparer.Ordinal);
        ((INotifyPropertyChanged)vs.Window).PropertyChanged += (_, e) => { lock (told) told.Add(e.PropertyName ?? ""); };
        var before = Members(vs.Window);

        vs.Window.ApplyLabels();

        var after = Members(vs.Window);
        var changed = after.Where(kv => !Equals(before[kv.Key], kv.Value)).Select(kv => kv.Key).ToList();
        Assert.True(changed.Count >= 30, $"witness: ApplyLabels changed only {changed.Count} members — the reading is not alive");
        var silent = changed.Where(n => !told.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(silent.Count == 0, "set by ApplyLabels without telling Visual Studio: " + string.Join(", ", silent));
    }
}
