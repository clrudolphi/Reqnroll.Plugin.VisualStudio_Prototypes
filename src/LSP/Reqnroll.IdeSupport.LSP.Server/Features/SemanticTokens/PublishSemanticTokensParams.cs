using System;

namespace Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

/// <summary>
/// Payload for the <c>reqnroll/semanticTokens</c> server-to-client notification.
/// </summary>
/// <remarks>
/// The server pushes this to the Visual Studio client whenever a feature file's tokens change,
/// because VS's built-in LSP semantic-token colorizer cannot map Reqnroll's custom token types
/// and does not reliably pull tokens. The VS client decodes <see cref="Data"/> with the legend
/// from the <c>initialize</c> response and colours the file via its own classifier. Other IDE
/// clients ignore this notification and use the standard pull-based semantic tokens flow.
/// </remarks>
public sealed class PublishSemanticTokensParams
{
    /// <summary>The document URI these tokens apply to.</summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>The document version the tokens were encoded from.</summary>
    public int Version { get; set; }

    /// <summary>The LSP semantic-token data (flat 5-int relative encoding).</summary>
    public int[] Data { get; set; } = Array.Empty<int>();
}
