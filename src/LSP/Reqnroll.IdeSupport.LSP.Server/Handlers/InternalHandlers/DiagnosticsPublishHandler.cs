using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
using Reqnroll.IdeSupport.LSP.Server.Services;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.InternalHandlers;

/// <summary>
/// Handles <see cref="MatchCacheChangedNotification"/> by aggregating the current parse-error
/// tags and binding-mismatch data for the affected feature file and pushing a
/// <c>textDocument/publishDiagnostics</c> notification to the IDE client.
/// </summary>
/// <remarks>
/// Fires alongside <see cref="SemanticTokensRefreshHandler"/> and
/// <see cref="SemanticTokensPushHandler"/> on every <see cref="MatchCacheChangedNotification"/>.
/// No ordering guarantee between these handlers is required — they are independent.
///
/// The LSP specification requires that a single <c>publishDiagnostics</c> message delivers the
/// <em>complete</em> diagnostic set for a URI; sending a partial set clears the diagnostics not
/// included.  The <see cref="IDiagnosticsAggregator"/> combines both sources (parse errors and
/// binding mismatches) into one list before this handler sends the single push.
///
/// For shared/linked features the primary owner's match set is used (Q18 2A/2B: LSP provides
/// one result per URI with no project context, so one owner must be chosen).
/// </remarks>
public sealed class DiagnosticsPublishHandler : INotificationHandler<MatchCacheChangedNotification>
{
    private readonly IDocumentBufferService   _documentBufferService;
    private readonly IBindingMatchService     _bindingMatchService;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IDiagnosticsAggregator   _aggregator;
    private readonly ILanguageServerFacade    _languageServer;
    private readonly IDeveroomLogger           _logger;

    public DiagnosticsPublishHandler(
        IDocumentBufferService    documentBufferService,
        IBindingMatchService      bindingMatchService,
        ILspWorkspaceScopeManager scopeManager,
        IDiagnosticsAggregator    aggregator,
        ILanguageServerFacade     languageServer,
        IDeveroomLogger            logger)
    {
        _documentBufferService = documentBufferService;
        _bindingMatchService   = bindingMatchService;
        _scopeManager          = scopeManager;
        _aggregator            = aggregator;
        _languageServer        = languageServer;
        _logger                = logger;
    }

    public Task Handle(MatchCacheChangedNotification notification, CancellationToken cancellationToken)
    {
        var uri = notification.Uri;

        if (!_documentBufferService.TryGet(uri, out var buffer) || buffer?.Tags is null)
        {
            _logger.LogVerbose($"DiagnosticsPublishHandler: no buffer/tags for {uri} — skipping.");
            return Task.CompletedTask;
        }

        // Look up the match set for the primary owner's (uri, project) slot.
        // Falls back to ProjectOwner.Unknown when no primary owner has been resolved yet
        // (pre-baseline startup period).
        var primaryOwner = _scopeManager.ResolvePrimaryOwner(uri);
        var matchKey = primaryOwner is not null
            ? new MatchSetKey(uri.ToString(),
                new ProjectOwner(primaryOwner.ProjectFullName, primaryOwner.TargetFrameworkMoniker))
            : MatchSetKey.ForUnknownProject(uri.ToString());

        _bindingMatchService.TryGet(matchKey, out var matchSet);
        // TryGet returns Empty when not found, so matchSet is never null here.

        var gherkinDiagnostics = _aggregator.Aggregate(buffer.Tags, matchSet);

        var lspDiagnostics = gherkinDiagnostics
            .Select(ToLspDiagnostic)
            .ToArray();

        _logger.LogVerbose(
            $"DiagnosticsPublishHandler: pushing {lspDiagnostics.Length} diagnostic(s) for {uri} v{notification.Version}");

        _languageServer.SendNotification(
            "textDocument/publishDiagnostics",
            new PublishDiagnosticsParams
            {
                Uri         = uri,
                Version     = notification.Version,
                Diagnostics = new Container<Diagnostic>(lspDiagnostics)
            });

        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Diagnostic ToLspDiagnostic(GherkinDiagnostic d)
        => new()
        {
            Range    = d.Range.ToLspRange(),
            Severity = d.Severity == GherkinDiagnosticSeverity.Error
                ? DiagnosticSeverity.Error
                : DiagnosticSeverity.Warning,
            Source  = d.Source,
            Message = d.Message
        };
}
