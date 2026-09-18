using System.IO;
using System.Linq;
using System.Reflection;
using Inferpal.Config;
using Inferpal.Services.Signals;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The contact points between the in-process half (net472, loaded by devenv) and the Core (net8,
/// out of its reach). Each is a place where two worlds talk to each other <b>with no compiler able
/// to keep them in agreement</b>: a rename on one side produces no error there, only a feature that
/// stops existing without a message. Same failure mode as the VSIX assets, guarded the same way.
/// </summary>
public class InProcContractTests
{
    // ── 1. The three settings the in-process half reads itself from config.json ────
    // InProcConfig cannot load InferpalConfig (net8): it re-reads the file and looks up three
    // properties by name. A rename on the Core side would disable ghost text silently - the reader
    // would find nothing and fall back to its defaults.

    [Theory]
    [InlineData("InlineCompletionEnabled", typeof(bool))]
    [InlineData("InlineCompletionMode",    typeof(string))]
    [InlineData("InlineCompletionModel",   typeof(string))]
    public void InProcConfig_ReadsPropertiesThatStillExistOnTheRealConfig(string name, Type expected)
    {
        var property = typeof(InferpalConfig).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

        Assert.True(property is not null,
            $"InferpalConfig.{name} no longer exists. Inferpal.InProc reads that name directly " +
            "from config.json (it cannot load the Core): without it, ghost text falls back to its " +
            "defaults instead of following the setting - with no message at all.");
        Assert.Equal(expected, property!.PropertyType);
    }

    [Fact]
    public void InProcConfig_UsesTheSameDefaultsAsTheRealConfig()
    {
        // The in-process reader applies its own defaults when the key is missing from the file
        // (the normal case: InferpalConfig only writes what has been touched).
        var reference = new InferpalConfig();

        Assert.True(reference.InlineCompletionEnabled);        // InProcConfig.Snapshot.Enabled
        Assert.Equal("Default", reference.InlineCompletionMode); // InProcConfig.Snapshot.Mode
        Assert.Equal(string.Empty, reference.InlineCompletionModel);
    }

    // ── 2. The operation names of the debugger bus ────────────────────────────────
    // The in-process driver and the out-of-process caller live in two assemblies AND two runtimes.
    // The literals live in DebugOps, compiled on both sides from the same file; this test checks
    // that the historical alias has not drifted.

    [Fact]
    public void DebugOps_AreTheOnlySourceOfTheWireNames()
    {
        var ops = typeof(Inferpal.Services.Debugging.DebugOps)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        Assert.NotEmpty(ops);
        Assert.Equal(ops.Count, ops.Values.Distinct().Count());   // no duplicate on the wire

        var session = typeof(Inferpal.Services.Debugging.SignalDebugSession)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith("Op"))
            .ToDictionary(f => f.Name.Substring(2), f => (string)f.GetRawConstantValue()!);

        Assert.Equal(ops.Count, session.Count);
        foreach (var pair in session)
            Assert.Equal(ops[pair.Key], pair.Value);
    }

    // ── 3. The inference sidecar ──────────────────────────────────────────────────
    // FimSidecar starts an executable by name. If it is not packaged, ghost text goes quiet.

    [Fact]
    public void Vsix_ShipsTheInProcAssemblyAndItsFimSidecar()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal", "Inferpal.csproj"));

        foreach (var file in new[] { "Inferpal.InProc.dll", "Inferpal.Fim.exe",
                                     "Inferpal.Fim.dll", "Inferpal.Fim.runtimeconfig.json" })
            Assert.True(csproj.Contains($"<Link>{file}</Link>"),
                $"{file} is no longer embedded in the VSIX by Inferpal.csproj. " +
                "The VSIX would still install, and the in-process half would be mute.");
    }

    // ── 5. Ghost text never inserts a completion whose origin it does not know ───

    /// <summary>
    /// <c>AcceptCompletion</c>'s position guard treats a <b>missing</b> snapshot as stale, not as
    /// "nothing to check".
    /// </summary>
    /// <remarks>
    /// <c>_triggerSnapshot</c> is set to null by <c>OnCaretMoved</c> and
    /// <c>OnTextChanged</c>, synchronously under the lock, while hiding the adornment goes through
    /// the dispatcher. In between, the completion is still "pending" and Tab inserted it <b>at the
    /// new caret</b>, skipping the guard — exactly the misplacement this block exists to prevent.
    ///
    /// The rule is textual: the controller needs a live <c>IWpfTextView</c>, hence a devenv. It
    /// targets the shape that decides — an <c>is null ||</c>, not an <c>is not null &amp;&amp;</c>.
    /// </remarks>
    [Fact]
    public void GhostText_TreatsAMissingTriggerSnapshotAsStale()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "Inferpal.InProc", "GhostText", "GhostTextController.cs"));

        // Witness: this really is the file that accepts the completion, not an emptied namesake.
        Assert.Contains("private void AcceptCompletion", source, StringComparison.Ordinal);
        Assert.Contains("_triggerSnapshot", source, StringComparison.Ordinal);

        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"var triggered = _triggerSnapshot;\s*\r?\n\s*if \(triggered is null \|\| !ReferenceEquals"),
            source);
    }

    // ── 5. The diff preview's three sentences do cross the boundary ───────────────
    //
    // The in-proc half has NO channel to the user: its Diagnostics ring is not the one /diagnostics
    // reads (out-of-process host), and it does not have Strings. Its three outcomes that apply
    // nothing — drift at write time, a Replace that throws, typing during the review — were
    // therefore mute for everyone, the user included. The remedy is a transport: the host composes,
    // the notice travels in the request, the renderer displays it. Two places can kill it without a
    // word, and no compiler would see it — those are the two rules below.

    /// <summary>
    /// Every sentence declared in <see cref="InlineDiffNotices"/> is actually displayed by the
    /// controller. The list comes from the TYPE, not from an enumeration written here: adding a
    /// fourth without wiring it must go red.
    /// </summary>
    [Fact]
    public void InlineDiff_EveryNotice_ReachesTheRenderer()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "Inferpal.InProc", "GhostText", "InlineDiffController.cs"));

        // Witness: this really is the diff preview's controller, and it keeps the shipped sentences.
        Assert.Contains("_notices", source, StringComparison.Ordinal);
        Assert.Contains("request.Notices", source, StringComparison.Ordinal);

        var names = typeof(InlineDiffNotices)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .ToList();
        Assert.True(names.Count >= 3, $"InlineDiffNotices declares only {names.Count} sentence(s): the rule checks nothing any more.");

        var missing = names.Where(n => !source.Contains($"_notices?.{n}", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            "Sentence(s) declared in InlineDiffNotices the controller never shows: "
            + string.Join(", ", missing)
            + ". An outcome that applies nothing and says nothing is indistinguishable from an ignored click.");
    }

    /// <summary>
    /// The host always sends them. The parameter is optional — which is what makes the silent
    /// omission possible, and it is the failure mode this repository pays for over and over: a
    /// repair written on one side, absent from the one that decides.
    /// </summary>
    [Fact]
    public void InlineDiff_TheHost_AlwaysSendsTheNotices()
    {
        var sites = 0;
        var offenders = new List<string>();
        var adapter = Path.Combine(RepoRoot(), "Inferpal");

        foreach (var file in Directory.EnumerateFiles(adapter, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax ma
                 || ma.Name.Identifier.ValueText != "WriteRequest"
                 || !ma.Expression.ToString().Contains("InlineDiffPreviewSignal")) continue;

                sites++;
                if (inv.ArgumentList.Arguments.Count < 4)
                    offenders.Add($"{Path.GetFileName(file)}({inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1})");
            }
        }

        Assert.True(sites >= 1, "No call to InlineDiffPreviewSignal.WriteRequest found in the VS adapter: the rule checks nothing any more.");
        Assert.True(offenders.Count == 0,
            "Preview request published WITHOUT its sentences: the renderer cannot compose them "
            + "(net472, no Strings), and its failure outcomes go silent again. Sites: "
            + string.Join(", ", offenders));
    }

    // ── 6. The debugger driver's loop never keeps the UI thread ───────────────────

    /// <summary>
    /// <c>VsDebugDriver.ServeAsync</c> returns to the thread pool <b>on every path</b>, between
    /// running a request and writing its response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every driver operation hops onto the UI thread to talk to EnvDTE, then comes back itself —
    /// but only when it succeeded. A COM exception (a breakpoint on a file VS cannot bind: an
    /// <b>ordinary</b> condition, which the loop's <c>catch</c> treats as an answer) skipped the
    /// return. The continuation stayed on the UI thread, and with it the whole loop:
    /// <c>WriteResponse</c> wrote its file and <c>ClaimRequest</c> polled the disk <b>at 10 Hz on
    /// devenv's message pump</b>, until the end of the session.
    /// </para>
    /// <para>
    /// ⚠ <b>A textual, single-subject rule, deliberately.</b> It needs an <c>IVsDebugger</c> and a
    /// live devenv: there is nothing to execute here. And it is deliberately <b>not</b> a convention
    /// rule sweeping the whole project — the three other files that hop onto the UI thread are
    /// one-shot handlers, which are allowed to end there; the subject is this loop, and only it.
    /// Same shape as the named assertion on <c>PruneFlattenedCoreSatelliteFromVsix</c>'s hook: what
    /// matters is the place, and its loss is silent.
    /// </para>
    /// </remarks>
    [Fact]
    public void DebugDriver_ServingLoop_ReturnsToThePoolOnEveryPath()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "Inferpal.InProc", "GhostText", "VsDebugDriver.cs"));

        // Witnesses: this really is that driver's serve loop, and it still hops onto the UI.
        Assert.Contains("private async Task ServeAsync", source, StringComparison.Ordinal);
        Assert.Contains("DebugCommandSignal.WriteResponse(response)", source, StringComparison.Ordinal);
        Assert.Contains("SwitchToMainThreadAsync", source, StringComparison.Ordinal);

        // The return to the pool is UNCONDITIONAL and precedes writing the response: neither inside
        // a `try`, nor on the one branch that succeeded.
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"await TaskScheduler\.Default\.SwitchTo\(\);\s*(?:\r?\n\s*)*"
                + @"Services\.Signals\.DebugCommandSignal\.WriteResponse\(response\);"),
            source);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
