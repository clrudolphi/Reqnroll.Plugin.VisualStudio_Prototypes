using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
using Reqnroll.IdeSupport.LSP.Server.Services;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.InternalHandlers;

/// <summary>
/// Handles <see cref="ReqnrollConfigChangedNotification"/> by re-parsing every open
/// feature file that belongs to the affected workspace root, then publishing a
/// <see cref="MatchCacheChangedNotification"/> for each so that semantic tokens
/// (and any other consumers) are refreshed.
/// </summary>
public class ReqnrollConfigChangedHandler : INotificationHandler<ReqnrollConfigChangedNotification>
{
    private readonly IDocumentBufferService _documentBufferService;
    private readonly IGherkinDocumentTaggerService _taggerService;
    private readonly IMediator _mediator;
    private readonly IDeveroomLogger _logger;

    public ReqnrollConfigChangedHandler(
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

    public async Task Handle(ReqnrollConfigChangedNotification notification, CancellationToken cancellationToken)
    {
        var affectedBuffers = _documentBufferService.All
            .Where(b => IsUnderWorkspaceRoot(b.Uri, notification.WorkspaceRootPath))
            .ToList();

        if (affectedBuffers.Count == 0)
        {
            _logger.LogVerbose($"ReqnrollConfigChanged for '{notification.WorkspaceRootPath}' — no open feature files to reparse.");
            return;
        }

        _logger.LogInfo($"ReqnrollConfigChanged for '{notification.WorkspaceRootPath}' — reparsing {affectedBuffers.Count} feature file(s).");

        foreach (var buffer in affectedBuffers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ParseAndNotifyAsync(buffer.Uri, buffer.Version, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ParseAndNotifyAsync(DocumentUri uri, int? version, CancellationToken cancellationToken)
    {
        // ParseAsync stores updated tags, recomputes/stores the binding match set, and
        // invalidates the semantic token cache internally before this notification fires.
        await _taggerService.ParseAsync(uri, version).ConfigureAwait(false);
        await _mediator.Publish(
            new MatchCacheChangedNotification(uri, version ?? 0),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsUnderWorkspaceRoot(DocumentUri uri, string workspaceRootPath)
    {
        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return false;

        return filePath.StartsWith(workspaceRootPath, StringComparison.OrdinalIgnoreCase);
    }
}
