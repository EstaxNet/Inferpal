using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Services.VsIntegration;

[VisualStudioContribution]
internal class ActiveDocumentTracker : ExtensionPart, ITextViewOpenClosedListener, ITextViewChangedListener
{
    private readonly VsContextHolder _contextHolder;

    public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
    {
        AppliesTo = [DocumentFilter.FromDocumentType(DocumentType.KnownValues.Text)],
    };

    public ActiveDocumentTracker(ExtensionCore extensionCore, VisualStudioExtensibility extensibility, VsContextHolder contextHolder)
        : base(extensionCore, extensibility)
    {
        _contextHolder = contextHolder;
    }

    public Task TextViewOpenedAsync(ITextViewSnapshot textView, CancellationToken ct)
    {
        _contextHolder.LatestView = textView;
        _contextHolder.RegisterOpen(textView.Document.Uri.LocalPath);
        return Task.CompletedTask;
    }

    public Task TextViewClosedAsync(ITextViewSnapshot textView, CancellationToken ct)
    {
        _contextHolder.RegisterClose(textView.Document.Uri.LocalPath);
        return Task.CompletedTask;
    }

    public Task TextViewChangedAsync(TextViewChangedArgs args, CancellationToken ct)
    {
        _contextHolder.LatestView = args.AfterTextView;
        return Task.CompletedTask;
    }
}
