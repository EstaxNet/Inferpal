using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.RpcContracts.Documents;

namespace Inferpal.Services.VsIntegration;

[VisualStudioContribution]
internal class ActiveDocumentTracker : ExtensionPart, ITextViewOpenClosedListener, ITextViewChangedListener, IDocumentEventsListener
{
    private readonly VsContextHolder _contextHolder;
    private IDisposable? _documentEvents;

    public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
    {
        AppliesTo = [DocumentFilter.FromDocumentType(DocumentType.KnownValues.Text)],
    };

    public ActiveDocumentTracker(ExtensionCore extensionCore, VisualStudioExtensibility extensibility, VsContextHolder contextHolder)
        : base(extensionCore, extensibility)
    {
        _contextHolder = contextHolder;
    }

    /// <summary>The document events, for the tab a person goes back to: no view opens or changes then (see
    /// <see cref="VsContextHolder.Shown"/>).</summary>
    protected override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await base.InitializeAsync(cancellationToken);
        try { _documentEvents = await Extensibility.Documents().SubscribeAsync(this, null, cancellationToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("ActiveDocumentTracker.Subscribe", ex); }
    }

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing) _documentEvents?.Dispose();
        base.Dispose(isDisposing);
    }

    public Task TextViewOpenedAsync(ITextViewSnapshot textView, CancellationToken ct)
    {
        _contextHolder.LatestView = textView;
        _contextHolder.RegisterOpen(textView.Document.Uri.LocalPath);
        return Task.CompletedTask;
    }

    public Task TextViewClosedAsync(ITextViewSnapshot textView, CancellationToken ct)
    {
        _contextHolder.ViewClosed(textView.Document.Uri.LocalPath);
        return Task.CompletedTask;
    }

    public Task TextViewChangedAsync(TextViewChangedArgs args, CancellationToken ct)
    {
        _contextHolder.LatestView = args.AfterTextView;
        return Task.CompletedTask;
    }

    public Task ShownAsync(DocumentEventArgs e, CancellationToken token)
    {
        if (e.Moniker is { IsFile: true } moniker) _contextHolder.Shown(moniker.LocalPath);
        return Task.CompletedTask;
    }

    public Task OpenedAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;
    public Task ClosedAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;
    public Task SavingAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;
    public Task SavedAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;
    public Task RenamedAsync(RenamedDocumentEventArgs e, CancellationToken token) => Task.CompletedTask;
    public Task HiddenAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;
}
