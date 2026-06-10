using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Notifications;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.InternalHandlers;

/// <summary>
/// Handles <see cref="MatchCacheChangedNotification"/> by asking the LSP client
/// to refresh its semantic tokens. No tag encoding is performed here; encoding is
/// deferred until the client sends a <c>textDocument/semanticTokens/full</c> request.
/// </summary>
/// <remarks>
/// The refresh is driven by the match-cache notification (rather than a raw parse
/// notification) so that it fires only after binding matches have been recomputed and
/// the binding-overlay tags are current; refreshing earlier would re-encode pre-binding
/// tokens. Multiple notifications can arrive in quick succession (e.g. when several files
/// open at once, or a build replaces the registry). A debounce window collapses those
/// bursts into a single <c>workspace/semanticTokens/refresh</c> request so the client is
/// not flooded. The refresh is also guarded by a client-capability check: if the client
/// did not advertise <c>workspace.semanticTokens.refreshSupport</c> the request is skipped.
/// </remarks>
public class SemanticTokensRefreshHandler : INotificationHandler<MatchCacheChangedNotification>
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    private readonly ILanguageServerFacade _languageServer;
    private readonly IDeveroomLogger _logger;

    private CancellationTokenSource? _debounceCts;
    private readonly object _debounceLock = new object();

    public SemanticTokensRefreshHandler(ILanguageServerFacade languageServer, IDeveroomLogger logger)
    {
        _languageServer = languageServer;
        _logger = logger;
    }

    public Task Handle(MatchCacheChangedNotification notification, CancellationToken cancellationToken)
    {
        // Guard: only send the refresh if the client advertised support for it.
        var semanticTokensWorkspace = _languageServer.ClientSettings.Capabilities?.Workspace?.SemanticTokens;
        if (semanticTokensWorkspace is null || !semanticTokensWorkspace.Value.IsSupported ||
            semanticTokensWorkspace.Value.Value?.RefreshSupport != true)
            return Task.CompletedTask;

        _logger.LogVerbose($"MatchCacheChanged: scheduling semantic token refresh for {notification.Uri} v{notification.Version}");

        // Debounce: cancel any pending refresh and start a new delayed one.
        CancellationTokenSource newCts;
        lock (_debounceLock)
        {
#pragma warning disable VSTHRD103 // Cancel() inside a lock; CancelAsync() cannot be awaited here
            _debounceCts?.Cancel();
#pragma warning restore VSTHRD103
            _debounceCts?.Dispose();
            newCts = _debounceCts = new CancellationTokenSource();
        }

        _ = SendRefreshAfterDelayAsync(newCts.Token);
        return Task.CompletedTask;
    }

    private async Task SendRefreshAfterDelayAsync(CancellationToken debounceToken)
    {
        try
        {
            await Task.Delay(DebounceDelay, debounceToken).ConfigureAwait(false);

            _logger.LogVerbose("SemanticTokensRefreshHandler: sending workspace/semanticTokens/refresh");
            await _languageServer.Client
                .SendRequest(WorkspaceNames.SemanticTokensRefresh)
                .ReturningVoid(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogVerbose("SemanticTokensRefreshHandler: debounce cancelled — superseded by newer notification");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"SemanticTokens refresh request failed: {ex.Message}");
        }
    }
}
