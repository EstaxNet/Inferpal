using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Host.SettingsWindow;
using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>Inferpal.Host --settings</c>: the settings form VS Code shows, in a browser page the host serves — for the editors
/// that have none (an Agent Client Protocol client), whose only setup was a console prompt. Driven here through a real
/// server on the loopback address, its host on the fake provider.
/// </summary>
public sealed class SettingsWindowTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-settings-window").FullName;
    private readonly string _web  = Directory.CreateTempSubdirectory("inferpal-settings-web").FullName;
    private readonly FakeInferenceProvider _fake = new() { ModelNames = ["test-model"] };
    private readonly HttpClient _http = new();
    private int _chats;
    private SettingsWindowServer _server = null!;

    public async Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_web, "settings.css"), "/* form */");
        File.WriteAllText(Path.Combine(_web, "settings-standalone.css"), "/* theme */");
        File.WriteAllText(Path.Combine(_web, "settings-standalone.js"), "window.formLoaded = true;");
        _fake.OnChat = (_, _) => { Interlocked.Increment(ref _chats); return Task.FromResult(new Models.ChatTurnResult("no", null, 0, 0)); };
        _server = await SettingsWindowServer.StartAsync(_root, _web, Host, CancellationToken.None);
    }

    private HostServer Host() =>
        new(_ => _fake, () => new InferpalConfig { RagEnabled = false, McpEnabled = false, DefaultModel = "test-model" });

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); Directory.Delete(_web, recursive: true); } catch { /* cleanup */ }
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> CallAsync(string method, object? args = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _server.Url + "rpc")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { method, @params = args }), Encoding.UTF8, "application/json"),
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        // A request refused before it reaches the relay (another site) is answered in plain text.
        var json = response.Content.Headers.ContentType?.MediaType == "application/json" && text.Length > 0;
        return (response.StatusCode, json ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    [Fact]
    public async Task ThePage_IsServedUnderItsToken_WithTheFormsOwnFiles()
    {
        var page = await _http.GetStringAsync(_server.Url);
        Assert.Contains("<script src=\"settings-standalone.js\"></script>", page, StringComparison.Ordinal);
        Assert.Contains("href=\"settings.css\"", page, StringComparison.Ordinal);
        Assert.Contains("window.__l10n = ", page, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", page, StringComparison.Ordinal);

        Assert.Equal("window.formLoaded = true;", await _http.GetStringAsync(_server.Url + "settings-standalone.js"));

        // Without the token, nothing: not the page, not a file.
        var port = new Uri(_server.Url).Port;
        foreach (var url in new[] { $"http://127.0.0.1:{port}/", $"http://127.0.0.1:{port}/0123456789abcdef/",
                                    $"http://127.0.0.1:{port}/settings-standalone.js", _server.Url + "config.json" })
            Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task AnotherSitesName_OrOrigin_IsRefused()
    {
        // DNS rebinding: a site whose name resolves to 127.0.0.1 sends ITS name; a cross-site request, its origin.
        using var rebound = new HttpRequestMessage(HttpMethod.Get, _server.Url);
        rebound.Headers.Host = "evil.example:" + new Uri(_server.Url).Port;
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(rebound)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync("config/get", origin: "http://evil.example")).Status);
        // Reference arm: the page's own origin.
        Assert.Equal(HttpStatusCode.OK, (await CallAsync("config/get", origin: new Uri(_server.Url).GetLeftPart(UriPartial.Authority))).Status);
    }

    [Fact]
    public async Task ThePage_ReachesTheSettings_AndNothingElse()
    {
        var (status, schema) = await CallAsync("settings/schema");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(schema.GetProperty("result").GetProperty("tabs").GetArrayLength() > 0);
        Assert.Equal(JsonValueKind.String, (await CallAsync("config/get")).Body.GetProperty("result").ValueKind);

        // A turn, a command, a tool: not a call of the settings form.
        foreach (var (method, args) in new (string, object)[]
                 {
                     ("chat/send", new { prompt = "delete everything" }), ("command/slash", new { text = "/commit-exec" }),
                     ("initialize", new { rootDir = "C:/" }),
                 })
        {
            var (refused, body) = await CallAsync(method, args);
            Assert.Equal(HttpStatusCode.Forbidden, refused);
            Assert.Contains(method, body.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        Assert.Equal(0, _chats);
    }

    [Fact]
    public async Task OpeningAFileThatIsNotThere_SaysSo()
    {
        var missing = Path.Combine(_root, "nope.md");
        var (status, body) = await CallAsync(SettingsWindowServer.OpenMethod, new { path = missing });
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(Strings.SettingsWindowNotFound(missing), body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheServer_EndsWhenThePageCloses()
    {
        await _http.PostAsync(_server.Url + "close", null);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await _server.WaitForCloseAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.InRange(clock.Elapsed, SettingsWindowServer.CloseGrace - TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AWindowNeverOpened_IsNotWaitedForEver()
    {
        // No request at all: past the first-visit limit, the wait ends.
        var calls = 0;
        await _server.WaitForCloseAsync(CancellationToken.None,
            () => DateTime.UtcNow + (Interlocked.Increment(ref calls) > 1 ? SettingsWindowServer.FirstVisitLimit * 2 : TimeSpan.Zero))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(_server.Visited);
    }

    [Fact]
    public async Task WithoutItsFiles_TheWindowSaysWhatIsMissing_AndDoesNotOpen()
    {
        var empty = Directory.CreateTempSubdirectory("inferpal-settings-noweb").FullName;
        try
        {
            using var output = new StringWriter();
            Assert.Equal(2, await SettingsWindowServer.RunAsync(_root, openBrowser: false, output, CancellationToken.None, Host, empty));
            Assert.Contains(Strings.SettingsWindowFilesMissing(empty), output.ToString(), StringComparison.Ordinal);
        }
        finally { Directory.Delete(empty, recursive: true); }
    }

    // ── One form, one bridge: the sources ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheBridge_IsSharedByVsCodeAndTheWindow_AndNeedsNoVsCode()
    {
        var bridge = WebviewRebuildTests.TsCode("settingsBridge.ts");
        Assert.DoesNotContain("from 'vscode'", bridge, StringComparison.Ordinal);
        Assert.Contains("case 'testConnection':", bridge, StringComparison.Ordinal);   // WITNESS: the switch lives here

        // VS Code's panel is its shell: the switch is not copied there.
        var panel = WebviewRebuildTests.TsCode("settingsPanel.ts");
        Assert.Contains("new SettingsBridge({", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("case 'testConnection':", panel, StringComparison.Ordinal);

        // The window's entry: its shell first (it defines acquireVsCodeApi), then the very form VS Code shows.
        var entry = WebviewRebuildTests.TsCode("webview/settingsStandalone.ts");
        var shell = entry.IndexOf("import './settingsStandaloneShell';", StringComparison.Ordinal);
        var form = entry.IndexOf("import './settings';", StringComparison.Ordinal);
        Assert.True(shell >= 0 && form > shell, "the form loads before the shell that gives it acquireVsCodeApi");
        Assert.Contains("new SettingsBridge({", WebviewRebuildTests.TsCode("webview/settingsStandaloneShell.ts"), StringComparison.Ordinal);
        Assert.Contains("outfile: 'media/settings-standalone.js'",
                        File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "esbuild.js")),
                        StringComparison.Ordinal);
    }

    /// <summary><c>/settings</c> opens each editor's own settings: VS Code runs its settings command on the host's effect,
    /// Visual Studio shows its settings window (an ACP thread's browser window: <c>AcpAgentTests</c>).</summary>
    [Fact]
    public void SlashSettings_OpensTheSettings_InEachEditor()
    {
        var chat = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        var effect = chat.IndexOf("case 'openSettings':", StringComparison.Ordinal);
        Assert.True(effect >= 0, "chatViewProvider.ts no longer handles the openSettings effect");
        var body = chat[effect..chat.IndexOf("break;", effect, StringComparison.Ordinal)];
        Assert.Contains("executeCommand('inferpal.openSettings')", body, StringComparison.Ordinal);
        Assert.Contains("\"command\": \"inferpal.openSettings\"",
                        File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "vscode", "package.json")), StringComparison.Ordinal);

        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(),
                                                               "Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"));
        Assert.Matches(@"case SlashCommandId\.Settings:\s*await _vs\.Shell\(\)\.ShowToolWindowAsync<InferpalSettingsToolWindow>", vm);
    }

    [Fact]
    public void TheWindowsCalls_AreTheServersAllowList_AndTheHostClientsMethods()
    {
        var shell = WebviewRebuildTests.TsCode("webview/settingsStandaloneShell.ts");
        var calls = Regex.Matches(shell, @"call(?:<[^>]*>)?\('([a-z]+/[A-Za-z]+)'").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(calls.Count >= 20, $"only {calls.Count} calls read: the rule reads nothing");   // WITNESS

        // Both ways: every call of the page is allowed, and nothing is allowed that the page does not call.
        calls.Remove(SettingsWindowServer.OpenMethod);
        Assert.Equal(calls.OrderBy(c => c), SettingsWindowServer.FormMethods.OrderBy(c => c));

        // And each is a method the host serves VS Code under that very name.
        var client = WebviewRebuildTests.TsCode("hostClient.ts");
        foreach (var method in calls)
            Assert.True(client.Contains($"sendRequest<", StringComparison.Ordinal) && client.Contains($"'{method}'", StringComparison.Ordinal),
                        $"{method} is not a call hostClient.ts makes");
    }
}
