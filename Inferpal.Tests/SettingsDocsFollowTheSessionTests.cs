using System.Text.Json;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The settings page's @Docs actions belong to the session open now, not to the one before a new <c>initialize</c>.
/// </summary>
/// <remarks>
/// They were built once per server, from the first session's settings and docs index. The settings window starts a new
/// session (another kind of model server saved in it, a reset): its @Docs page then listed the first session's sites,
/// and added, re-indexed or removed through a docs index already disposed.
/// </remarks>
public partial class HostServerTests
{
    [Fact]
    public async Task TheDocsPage_FollowsTheSessionOpenNow()
    {
        var sessions = 0;
        var vue = new DocSite("vue", "Vue", "https://vuejs.org/guide");
        using var h = CreateHarness(cfg =>
        {
            // The first session has no source; the one started after it has Vue.
            cfg.DocSitesJson = Interlocked.Increment(ref sessions) == 1 ? "" : JsonSerializer.Serialize(new[] { vue });
        });
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        static IEnumerable<string?> Ids(JsonElement page) =>
            page.GetProperty("sites").EnumerateArray().Select(s => s.GetProperty("id").GetString());
        var before = await h.Client.InvokeAsync<JsonElement>("settings/docsSites").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.DoesNotContain("vue", Ids(before));   // witness: the first session's page was built, without Vue

        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var after = await h.Client.InvokeAsync<JsonElement>("settings/docsSites").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Contains("vue", Ids(after));
    }
}
