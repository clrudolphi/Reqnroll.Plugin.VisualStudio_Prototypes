using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
using Reqnroll.IdeSupport.LSP.Server.Services;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

public class TextDocumentSyncHandler : TextDocumentSyncHandlerBase
{
    private readonly IDocumentBufferService _documentBufferService;
    private readonly IGherkinDocumentTaggerService _taggerService;
    private readonly IMediator _mediator;
    private readonly IDeveroomLogger _logger;

    private static readonly TextDocumentSelector _featureFileSelector = new(
        new TextDocumentFilter
        {
            Pattern = "**/*.feature"
        }
    );

    public TextDocumentSyncHandler(
        IDocumentBufferService documentBufferService,
        IGherkinDocumentTaggerService taggerService,
        IMediator mediator,
        IDeveroomLogger logger)
    {
        _documentBufferService = documentBufferService;
        _taggerService = taggerService;
        _mediator = mediator;
        _logger = logger;
    }

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
        => new(uri, "gherkin");

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = _featureFileSelector,
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions { IncludeText = false }
        };

    public override async Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var version = request.TextDocument.Version;
        var text = request.TextDocument.Text;

        _logger.LogInfo($"Document opened: {uri} (version {version})");
        _documentBufferService.Update(uri, version, text);

        await ParseAndNotifyAsync(uri, version, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }

    public override async Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var version = request.TextDocument.Version;

        // With TextDocumentSyncKind.Full the single change contains the full document text.
        var text = request.ContentChanges.LastOrDefault()?.Text ?? string.Empty;

        _logger.LogInfo($"Document changed: {uri} (version {version})");
        _documentBufferService.Update(uri, version, text);

        await ParseAndNotifyAsync(uri, version, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInfo($"Document saved: {request.TextDocument.Uri}");
        // Re-parse on save when text is not sent on change (e.g., incremental sync scenarios).
        // With full sync the buffer is already up to date; nothing extra needed.
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        _logger.LogInfo($"Document closed: {uri}");
        _documentBufferService.Remove(uri);

        return Unit.Task;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task ParseAndNotifyAsync(DocumentUri uri, int? version, CancellationToken cancellationToken)
    {
        var tags = await _taggerService.ParseAsync(uri, version).ConfigureAwait(false);
        _documentBufferService.UpdateTags(uri, tags);
        await _mediator.Publish(
            new GherkinDocumentParsedNotification(uri, version ?? 0, tags),
            cancellationToken).ConfigureAwait(false);
    }
}
