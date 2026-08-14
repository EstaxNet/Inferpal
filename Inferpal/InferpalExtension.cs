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

    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        // ⚠ C'est CE bloc (pas source.extension.vsixmanifest) que le SDK Extensibility
        // sérialise en extension.vsixmanifest dans le VSIX. La validation Marketplace
        // exige que License pointe vers un fichier .txt/.rtf embarqué dans le package.
        // ⚠ La qualification `this.ExtensionAssemblyVersion` est OBLIGATOIRE : non qualifié,
        // le générateur du SDK ne substitue pas la version → Identity Version="0.0.0.0".
        Metadata = new(
            id: "Inferpal.bf3c1a2e-4d5f-4b8c-9e2a-1f7d3c6e8b4a",
            version: this.ExtensionAssemblyVersion,
            publisherName: "EstaxNet",
            displayName: "Inferpal",
            description: "AI developer assistant for self-hosted LLMs — Ollama, LM Studio, or any OpenAI-compatible server, on your machine or your own remote host. Autonomous agentic loop with 26 built-in tools: read/write files, run builds and tests, semantic codebase search, inline completions, query Git, browse the web — no mandatory cloud, no telemetry."
        )
        {
            License = "LICENSE.txt",
            Icon = @"assets\icon.png",
            MoreInfo = "https://github.com/EstaxNet/Inferpal",
            Tags = ["AI", "LLM", "Ollama", "LM Studio", "Assistant", "Code", "Local", "Agentic", "Autocomplete", "RAG"],
            Preview = false,
        }
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
