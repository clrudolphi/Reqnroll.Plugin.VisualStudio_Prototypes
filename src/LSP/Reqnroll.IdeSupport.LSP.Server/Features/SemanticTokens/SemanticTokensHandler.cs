using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Services;

namespace Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

/// <summary>
/// Handles <c>textDocument/semanticTokens/full</c>, <c>textDocument/semanticTokens/full/delta</c>,
/// and <c>textDocument/semanticTokens/range</c> requests by delegating to <see cref="ISemanticTokenService"/>.
/// </summary>
public class SemanticTokensHandler
{
    private readonly ISemanticTokenService _semanticTokenService;
    private readonly IDocumentBufferService _documentBufferService;
    private readonly IDeveroomLogger _logger;

    public SemanticTokensHandler(
        ISemanticTokenService semanticTokenService,
        IDocumentBufferService documentBufferService,
        IDeveroomLogger logger)
    {
        _semanticTokenService = semanticTokenService;
        _documentBufferService = documentBufferService;
        _logger = logger;
    }

    // ── Full ──────────────────────────────────────────────────────────────────

    public async Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?> HandleAsync(
        SemanticTokensParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        if (!IsFeatureFile(uri)) return null;
        var version = GetCurrentVersion(uri);

        _logger.LogVerbose($"SemanticTokens/full requested for {uri} (version {version})");

        return await _semanticTokenService.GetSemanticTokensAsync(uri, version, cancellationToken)
                                          .ConfigureAwait(false);
    }

    // ── Delta ─────────────────────────────────────────────────────────────────
    // We don't maintain delta state; return the full token set wrapped in SemanticTokensFullOrDelta.

    public async Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokensFullOrDelta?> HandleAsync(
        SemanticTokensDeltaParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        if (!IsFeatureFile(uri)) return null;
        var version = GetCurrentVersion(uri);

        _logger.LogVerbose($"SemanticTokens/full/delta requested for {uri} (version {version}), returning full tokens");

        var tokens = await _semanticTokenService.GetSemanticTokensAsync(uri, version, cancellationToken)
                                                .ConfigureAwait(false);

        return tokens is null ? null : new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokensFullOrDelta(tokens);
    }

    // ── Range ─────────────────────────────────────────────────────────────────
    // Return all tokens; the client will filter by range.

    public async Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?> HandleAsync(
        SemanticTokensRangeParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        if (!IsFeatureFile(uri)) return null;
        var version = GetCurrentVersion(uri);

        _logger.LogVerbose($"SemanticTokens/range requested for {uri} (version {version})");

        return await _semanticTokenService.GetSemanticTokensAsync(uri, version, cancellationToken)
                                          .ConfigureAwait(false);
    }

    // ── SemanticTokensHandlerBase abstract members ────────────────────────────
    // These are used by the base-class builder pattern; we bypass it by overriding
    // Handle directly, so these overloads are never called in practice.

    //protected override Task Tokenize(
    //    SemanticTokensBuilder builder,
    //    ITextDocumentIdentifierParams identifier,
    //    CancellationToken cancellationToken)
    //    => Task.CompletedTask; // not used – Handle overrides bypass the builder

    //protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
    //    ITextDocumentIdentifierParams @params,
    //    CancellationToken cancellationToken)
    //    => Task.FromResult(new SemanticTokensDocument(_semanticTokenService.Legend));

    //// ── Registration options ──────────────────────────────────────────────────

    //protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
    //    SemanticTokensCapability capability,
    //    ClientCapabilities clientCapabilities)
    //{
    //    // Return options only if dynamic registration is NOT supported;
    //    // static path is handled via OnInitialized
    //    if (clientCapabilities.TextDocument?.SemanticTokens.Value?.DynamicRegistration == true)
    //        return null!;

    //    return new SemanticTokensRegistrationOptions
    //    {

    //        Id = "reqnroll-semantic-tokens",
    //        DocumentSelector = new TextDocumentSelector(
    //            new TextDocumentFilter { Pattern = "**/*.feature" }),
    //        Legend = _semanticTokenService.Legend,
    //        Full = new SemanticTokensCapabilityRequestFull { Delta = false },
    //        Range = false
    //    };
    //}

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsFeatureFile(OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri uri)
        => uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);

    private int GetCurrentVersion(OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri uri)
    {
        if (_documentBufferService.TryGet(uri, out var buffer) && buffer?.Version is int v)
            return v;

        return 0;
    }
}
