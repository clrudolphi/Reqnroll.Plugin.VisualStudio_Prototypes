using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Classification;

/// <summary>One semantic token in absolute document coordinates, with its legend type name.</summary>
internal readonly struct ClassifiedToken
{
    public ClassifiedToken(int line, int startChar, int length, string tokenType)
    {
        Line = line;
        StartChar = startChar;
        Length = length;
        TokenType = tokenType;
    }

    public int Line { get; }
    public int StartChar { get; }
    public int Length { get; }

    /// <summary>The legend token-type name, e.g. <c>reqnroll.keyword</c>.</summary>
    public string TokenType { get; }
}

/// <summary>
/// Process-wide cache of the decoded LSP semantic tokens for each open <c>.feature</c> file,
/// shared between the LSP message interceptor (writer) and the editor classifier (reader).
/// </summary>
/// <remarks>
/// <para>
/// Visual Studio's built-in LSP semantic-token colorizer maps token-type names to classifications
/// through a fixed internal table, so it cannot honour Reqnroll's custom <c>reqnroll.*</c> token
/// types (they all fall back to plain "text").  To restore the custom Reqnroll colours we bypass
/// that path entirely: <see cref="SemanticTokensClassificationInterceptor"/> observes the server's
/// semantic-token responses as they flow through the connection and records them here, and
/// <see cref="GherkinSemanticClassifier"/> reads them back and produces classification spans against
/// the <c>DeveroomClassifications</c> classification types of the same name.
/// </para>
/// <para>
/// A static singleton is used (rather than MEF) because the interceptor is constructed by the
/// non-MEF <c>ReqnrollLanguageClient</c> while the classifier is created by the editor's MEF host;
/// both need the same instance.
/// </para>
/// </remarks>
internal sealed class SemanticTokenClassificationStore
{
    public static SemanticTokenClassificationStore Instance { get; } = new SemanticTokenClassificationStore();

    private string[] _legend = Array.Empty<string>();
    private readonly ConcurrentDictionary<string, IReadOnlyList<ClassifiedToken>> _byFile =
        new ConcurrentDictionary<string, IReadOnlyList<ClassifiedToken>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after the tokens for a file change. Argument is the normalized file key.</summary>
    public event Action<string>? TokensChanged;

    /// <summary>The token-type legend advertised by the server (index → name).</summary>
    public string[] Legend => _legend;

    public void SetLegend(string[] tokenTypes) => _legend = tokenTypes ?? Array.Empty<string>();

    public void SetTokens(string fileKey, IReadOnlyList<ClassifiedToken> tokens)
    {
        if (fileKey is null) return;
        _byFile[fileKey] = tokens;
        TokensChanged?.Invoke(fileKey);
    }

    public bool TryGetTokens(string fileKey, out IReadOnlyList<ClassifiedToken> tokens)
    {
        if (fileKey is not null && _byFile.TryGetValue(fileKey, out var found))
        {
            tokens = found;
            return true;
        }

        tokens = Array.Empty<ClassifiedToken>();
        return false;
    }

    /// <summary>
    /// Normalizes either a <c>file://</c> URI or a local path to a stable comparison key
    /// (absolute, lower-cased) so the interceptor (which sees URIs) and the classifier
    /// (which sees file paths) agree.
    /// </summary>
    public static string? NormalizeKey(string? pathOrUri)
    {
        if (string.IsNullOrEmpty(pathOrUri)) return null;
        try
        {
            string? path = Uri.TryCreate(pathOrUri, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : pathOrUri;
            return Path.GetFullPath(path).ToLowerInvariant();
        }
        catch
        {
            return pathOrUri!.ToLowerInvariant();
        }
    }
}
