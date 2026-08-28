using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

namespace Inferpal;

/// <summary>
/// Extension entry point. Registers all services in the DI container and declares metadata
/// consumed by the VS Extensibility SDK to generate the VSIX manifest.
/// </summary>
/// <remarks>
/// Services registered here are available as constructor parameters in all
/// <see cref="Microsoft.VisualStudio.Extensibility.ExtensionPart"/> types
/// (Commands, ToolWindows, etc.) via the VS Extensibility SDK's built-in DI container.
/// </remarks>
[VisualStudioContribution]
public class InferpalExtension : Extension
{
    // Hooked as early as possible: WPF windows (code-action spinner, inline-edit dialog)
    // lazily bind System.Windows.Extensions, unresolvable in the host's extension ALC.
    static InferpalExtension() => WpfAssemblyResolver.Install();

    /// <summary>
    /// ⚠ <b>This <c>true</c> is a packaging switch we have to live with, not a hosting
    /// intention.</b> The SDK requires <c>RequiresInProcessHosting = true</c> as soon as
    /// <c>&lt;VssdkCompatibleExtension&gt;</c> is true (<c>VSEXT0007</c>) — and
    /// <c>VssdkCompatibleExtension</c> is what makes <c>source.extension.vsixmanifest</c> get
    /// packaged, hence the <c>&lt;Assets&gt;</c> section without which the in-process half is
    /// never inventoried. The compiler will not let the pair be split.
    /// </summary>
    /// <remarks>
    /// ⚠ What the SDK infers from it is <c>"allowHostingInProcess": true</c> for every service in
    /// <c>.vsextension\extension.json</c> — and VS takes that at its word: in the
    /// <c>ActivityLog</c> of the live hive, <c>ExtensionMetadataInProcServiceBroker</c> tried to
    /// create <b>inside devenv</b> <c>Inferpal.Services.VsIntegration.ActiveDocumentTracker</c>
    /// and failed on <c>FileNotFoundException: System.Runtime, Version=8.0.0.0</c> from
    /// <c>InferpalExtension..cctor()</c>. Same verdict as in MEF, one floor up: <b>nothing net8
    /// activates inside devenv</b>. That is why the build sets this field back to <c>false</c> in
    /// the generated JSON — target <c>ForceOutOfProcessHostingInExtensionJson</c>
    /// (Inferpal.csproj).
    /// </remarks>
    /// <remarks>
    /// The <c>net472 + VssdkCompatibleExtension + in-proc</c> triple is the contract for
    /// everything devenv hosts: the official project template
    /// (<c>VisualStudioExtensibilityInProcessProject</c>) targets <c>net472</c>, the two hybrids
    /// Microsoft ships (Copilot Build Analyzer, Copilot testing) are <c>net472</c> all the way to
    /// their Extensibility assembly, and the extension with the <b>same shape as ours</b> —
    /// AppModernizationForDotNet — is <c>net10.0</c> with every service at
    /// <c>allowHostingInProcess: false</c>, delegating what must live inside devenv to a
    /// <b>separate net472 container</b>. Here that container is <c>Inferpal.InProc.dll</c> —
    /// ghost text, inline diff preview, the <c>/tdd</c> debugger driver. This assembly stays
    /// out-of-process, in the host VS starts alongside.
    /// </remarks>
    /// <remarks>
    /// ⚠ <see cref="ExtensionConfiguration.Metadata"/> MUST stay null while
    /// <c>RequiresInProcessHosting</c> is true (<c>CEE0028</c>). The listing metadata (id,
    /// version, license, icon, tags, description) therefore lives in
    /// <c>source.extension.vsixmanifest</c>, the manifest that is actually packaged; the
    /// "N built-in tools" count there is locked by <c>DocCountersTests</c>.
    /// </remarks>
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        RequiresInProcessHosting = true,
    };

    /// <summary>
    /// Kills the child processes this extension spawned. The DI container disposes
    /// <see cref="IDisposable"/> singletons, but <see cref="McpToolService"/> is
    /// <see cref="IAsyncDisposable"/>-only — a synchronous container teardown skips it, and an MCP
    /// server started with <c>UseShellExecute=false</c> does not die with the extension host.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { this.ServiceProvider?.GetService<McpToolService>()?.KillAllServers(); }
            catch (Exception ex) { Diagnostics.Swallow("InferpalExtension.DisposeMcp", ex); }

            try { this.ServiceProvider?.GetService<ToolRegistry>()?.Dispose(); }
            catch (Exception ex) { Diagnostics.Swallow("InferpalExtension.DisposeTools", ex); }
        }
        base.Dispose(disposing);
    }

    protected override void InitializeServices(IServiceCollection services)
    {
        base.InitializeServices(services);

        // §22 tranche 2: family-A signal channels are scoped by devenv PID. Out-of-process, that
        // key is our parent (probe 6, C2/C3) — but the direct-child topology is empirical, not
        // contractual, so the key is only declared after checking the parent actually IS devenv.
        // On failure (lookup error, unexpected parent) no key is declared: this host keeps the
        // legacy unscoped names while the in-process side scopes with its own PID, so the pair is
        // LOST until VS restarts — solution rooting, build banner and diff previews go silent.
        // That is why both branches trace to /diagnostics instead of failing mutely.
        try
        {
            var ppid = ParentProcess.GetParentProcessId();
            using var parent = System.Diagnostics.Process.GetProcessById(ppid);
            if (parent.ProcessName.Equals("devenv", StringComparison.OrdinalIgnoreCase))
                SignalScope.DeclareVsInstance(ppid);
            else
                Diagnostics.Swallow("InferpalExtension.DeclareVsInstance", new InvalidOperationException(
                    $"Parent '{parent.ProcessName}' (pid {ppid}) is not devenv; family-A signals stay unscoped and unpaired."));
        }
        catch (Exception ex) { Diagnostics.Swallow("InferpalExtension.DeclareVsInstance", ex); }

        services.AddSingleton(_ => InferpalConfig.Load());
        // Resolve the active inference backend (Ollama or OpenAI-compatible) from config.Provider.
        services.AddSingleton<IInferenceProvider>(sp =>
            InferenceProviderFactory.Create(sp.GetRequiredService<InferpalConfig>()));
        services.AddSingleton<VsContextHolder>();
        services.AddSingleton<Services.Editor.IEditorSurface, VsEditorSurface>();
        services.AddSingleton<IApprovalService, VsApprovalService>();
        services.AddSingleton<ProjectIndexService>();
        services.AddSingleton<ProjectMapService>();
        services.AddSingleton<LspSemanticProvider>();
        services.AddSingleton<McpToolService>();
        services.AddSingleton<DocsIndexService>();
        // Roadmap §21. Registering it makes the two debug tools appear in the registry; the
        // session itself answers "unavailable" until an in-process driver advertises itself, so a
        // devenv whose package failed to load degrades to no debugger rather than to a hang.
        services.AddSingleton<Services.Debugging.IDebugSession, Services.Debugging.SignalDebugSession>();
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<ModelLifetimeService>();
        services.AddSingleton<VsBuildMonitor>();
    }
}
