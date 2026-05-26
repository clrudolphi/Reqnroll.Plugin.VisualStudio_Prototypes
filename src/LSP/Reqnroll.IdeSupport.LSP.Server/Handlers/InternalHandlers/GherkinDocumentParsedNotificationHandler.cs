using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Notifications;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.InternalHandlers;

/// <summary>
/// Handles <see cref="GherkinDocumentParsedNotification"/> by asking the LSP client
/// to refresh its semantic tokens. No tag encoding is performed here; encoding is
/// deferred until the client sends a <c>textDocument/semanticTokens/full</c> request.
/// </summary>
public class GherkinDocumentParsedNotificationHandler : INotificationHandler<GherkinDocumentParsedNotification>
{
    private readonly ILanguageServerFacade _languageServer;
    private readonly IDeveroomLogger _logger;

    public GherkinDocumentParsedNotificationHandler(ILanguageServerFacade languageServer, IDeveroomLogger logger)
    {
        _languageServer = languageServer;
        _logger = logger;
    }

    public Task Handle(GherkinDocumentParsedNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogVerbose($"GherkinDocumentParsed: requesting semantic token refresh for {notification.Uri} v{notification.Version}");

        // Fire-and-forget: notify the client to re-request tokens; do not await so
        // the MediatR pipeline is not held open for a network round-trip.
        _ = SendRefreshAsync();
        return Task.CompletedTask;
    }

    private async Task SendRefreshAsync()
    {
        try
        {
            await _languageServer.Client
                .SendRequest(WorkspaceNames.SemanticTokensRefresh)
                .ReturningVoid(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"SemanticTokens refresh notification failed: {ex.Message}");
        }
    }
}
