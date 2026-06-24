using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

/// <summary>
/// Maintains a per-document cache of LSP semantic tokens encoded from Gherkin
/// <see cref="Reqnroll.IdeSupport.LSP.Core.Editor.Services.Parsing.GherkinDocuments.DeveroomTag"/> instances.
/// Encoding is deferred until <see cref="GetSemanticTokensAsync"/> is called; tags are
/// read directly from <see cref="IDocumentBufferService"/>.
/// </summary>
public interface ISemanticTokenService
{
    /// <summary>The shared legend that must be returned by the server's initialize response.</summary>
    SemanticTokensLegend Legend { get; }

    /// <summary>
    /// Returns the cached encoded token data for the requested document version,
    /// or <see langword="null"/> when no data is available yet.
    /// </summary>
    Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?> GetSemanticTokensAsync(DocumentUri uri, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts any cached token result for <paramref name="uri"/>, forcing the next
    /// <see cref="GetSemanticTokensAsync"/> call to re-encode from the current tags.
    /// Call this whenever the document's tags are updated without a version bump
    /// (e.g. after binding discovery completes for an already-open file).
    /// </summary>
    void InvalidateCache(DocumentUri uri);
}
