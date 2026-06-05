using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
using Reqnroll.IdeSupport.LSP.Server.Services;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

/// <summary>
/// Single OmniSharp text-document sync handler covering both document types the server cares
/// about: <c>.feature</c> files (parsed into the Gherkin document buffer) and <c>.cs</c> files
/// (fed to Roslyn source-level binding discovery, design doc feature F2). Both are registered
/// here so that a single sync handler owns text synchronization; the per-document branching is
/// done by file extension.
/// </summary>
public class TextDocumentSyncHandler : TextDocumentSyncHandlerBase
{
    private readonly IDocumentBufferService _documentBufferService;
    private readonly IGherkinDocumentTaggerService _taggerService;
    private readonly IBindingMatchService _bindingMatchService;
    private readonly ICSharpBindingDiscoveryService _csharpDiscoveryService;
    private readonly IMediator _mediator;
    private readonly IDeveroomLogger _logger;

    private static readonly TextDocumentSelector _documentSelector = new(
        new TextDocumentFilter { Pattern = "**/*.feature" },
        // The server registers interest in .cs files only to drive Roslyn binding re-discovery;
        // it does not provide general C# language features. See design doc §5 "Document Scope".
        new TextDocumentFilter { Pattern = "**/*.cs" }
    );

    public TextDocumentSyncHandler(
        IDocumentBufferService documentBufferService,
        IGherkinDocumentTaggerService taggerService,
        IBindingMatchService bindingMatchService,
        ICSharpBindingDiscoveryService csharpDiscoveryService,
        IMediator mediator,
        IDeveroomLogger logger)
    {
        _documentBufferService = documentBufferService;
        _taggerService = taggerService;
        _bindingMatchService = bindingMatchService;
        _csharpDiscoveryService = csharpDiscoveryService;
        _mediator = mediator;
        _logger = logger;
    }

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
        => new(uri, IsCSharp(uri) ? "csharp" : "gherkin");

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = _documentSelector,
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions { IncludeText = false }
        };

    public override async Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var version = request.TextDocument.Version;
        var text = request.TextDocument.Text;

        if (IsCSharp(uri))
        {
            _logger.LogInfo($"C# document opened: {uri} (version {version})");
            await _csharpDiscoveryService.UpdateFromSourceAsync(uri, text, cancellationToken).ConfigureAwait(false);
            return Unit.Value;
        }

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

        if (IsCSharp(uri))
        {
            _logger.LogInfo($"C# document changed: {uri} (version {version})");
            await _csharpDiscoveryService.UpdateFromSourceAsync(uri, text, cancellationToken).ConfigureAwait(false);
            return Unit.Value;
        }

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

        // .cs files are not tracked in the Gherkin document buffer; their last-discovered bindings
        // are intentionally retained after close (a rebuild, not a close, supersedes them).
        if (IsCSharp(uri))
            return Unit.Task;

        _logger.LogInfo($"Document closed: {uri}");
        _documentBufferService.Remove(uri);
        _bindingMatchService.Invalidate(uri.ToString());

        return Unit.Task;
    }

    private static bool IsCSharp(DocumentUri uri) =>
        uri.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task ParseAndNotifyAsync(DocumentUri uri, int? version, CancellationToken cancellationToken)
    {
        // ParseAsync stores updated tags, recomputes/stores the binding match set, and
        // invalidates the semantic token cache internally before this notification fires.
        await _taggerService.ParseAsync(uri, version).ConfigureAwait(false);
        await _mediator.Publish(
            new MatchCacheChangedNotification(uri, version ?? 0),
            cancellationToken).ConfigureAwait(false);
    }
}
