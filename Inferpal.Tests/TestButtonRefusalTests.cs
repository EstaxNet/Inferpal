using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The settings' Test button is where a user checks an API key, and a wrong key answers 401 — "✗ Unreachable" sent
/// them to check a server that was up, in Visual Studio and in VS Code alike. The probe now keeps the refusal, and both
/// panels show it.
/// </summary>
public class TestButtonRefusalTests
{
    [Fact]
    public async Task AKeyTheServerRefuses_IsNamed_AndWinsOverThe404sOfTheOtherBackendsEndpoints()
    {
        // An OpenAI-compatible server with a key: its own endpoints answer 401, the other backends' paths 404.
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/", StringComparison.Ordinal) ? "{\"error\":\"invalid api key\"}" : "not found",
            path => path.StartsWith("/v1/", StringComparison.Ordinal) ? 401 : 404);

        var (provider, refusal) = await ProviderProbe.DetectWithRefusalAsync(server.BaseUrl, "wrong", CancellationToken.None);

        Assert.Null(provider);
        Assert.StartsWith("HTTP 401", refusal);
    }

    [Fact]
    public async Task ADetectedBackend_CarriesNoRefusal()
    {
        // Reference arm: the probes of the other backends' endpoints 404 on the way to a detection — not a refusal.
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/models", StringComparison.Ordinal) ? "{\"data\":[{\"id\":\"m\"}]}" : "not found",
            path => path.StartsWith("/v1/models", StringComparison.Ordinal) ? 200 : 404);

        var (provider, refusal) = await ProviderProbe.DetectWithRefusalAsync(server.BaseUrl, null, CancellationToken.None);

        Assert.Equal(InferenceProviderFactory.OpenAiCompatible, provider);
        Assert.Null(refusal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void BothTestButtons_ShowTheRefusal()
    {
        var vs = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));
        Assert.Contains("ProviderProbe.DetectWithRefusalAsync(", vs, StringComparison.Ordinal);
        Assert.Contains("Strings.StatusRefused(", vs, StringComparison.Ordinal);

        var host = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), "Inferpal.Host", "HostServer.cs"));
        Assert.Contains("ProviderProbe.DetectWithRefusalAsync(", host, StringComparison.Ordinal);

        var webview = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(RepoRoot(), "vscode", "src", "webview", "settings.ts")));
        var at = webview.IndexOf("case 'testResult'", StringComparison.Ordinal);
        Assert.True(at >= 0, "the Test result is no longer handled by the settings webview");      // WITNESS
        Assert.Matches(new Regex(@"msg\.refused\s*\?\?\s*t\('Backend unreachable'\)"), webview[at..]);
    }
}
