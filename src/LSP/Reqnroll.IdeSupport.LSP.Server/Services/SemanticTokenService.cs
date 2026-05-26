using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Document;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Parsing.GherkinDocuments;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Reqnroll.IdeSupport.LSP.Server.Services;

/// <summary>
/// Maps <see cref="DeveroomTag"/> instances to LSP semantic token integer tuples
/// on demand and caches the encoded result per document version.
/// Encoding is deferred until the client sends a semantic tokens request.
/// </summary>
public sealed class SemanticTokenService : ISemanticTokenService
{
    // ── Legend ────────────────────────────────────────────────────────────────
    // Token type indices must match the order of the _tokenTypes array below.
    private static readonly ImmutableArray<SemanticTokenType> _tokenTypes =
    [
        SemanticTokenType.Keyword,      // 0 – DefinitionLineKeyword, StepKeyword
        SemanticTokenType.String,       // 1 – Description, DocString
        SemanticTokenType.Parameter,    // 2 – StepParameter, ScenarioOutlinePlaceholder
        SemanticTokenType.Variable,     // 3 – Tag (@tag)
        SemanticTokenType.Comment,      // 4 – Comment
        SemanticTokenType.Class,        // 5 – FeatureBlock, RuleBlock, ScenarioDefinitionBlock, ExamplesBlock
        SemanticTokenType.Function,     // 6 – DefinedStep
        SemanticTokenType.Regexp,       // 7 – UndefinedStep
        SemanticTokenType.Struct,       // 8 – DataTable, DataTableHeader
        SemanticTokenType.Event,        // 9 – StepBlock
    ];

    private static readonly ImmutableArray<SemanticTokenModifier> _tokenModifiers =
    [
        SemanticTokenModifier.Declaration,  // 0 – block-level definition lines
        SemanticTokenModifier.Deprecated,   // 1 – UndefinedStep / BindingError
    ];

    private enum TokenType
    {
        Keyword = 0,
        String = 1,
        Parameter = 2,
        Variable = 3,
        Comment = 4,
        Class = 5,
        Function = 6,
        Regexp = 7,
        Struct = 8,
        Event = 9,
    }

    [Flags]
    private enum TokenModifier
    {
        None = 0,
        Declaration = 1 << 0,
        Deprecated = 1 << 1,
    }

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly IDocumentBufferService _documentBufferService;
    private readonly IDeveroomLogger _logger;

    // key: (uri, version)  value: encoded token data
    private readonly ConcurrentDictionary<(DocumentUri, int), SemanticTokens> _cache = new();

    public SemanticTokensLegend Legend { get; } = new SemanticTokensLegend
    {
        TokenTypes = new Container<SemanticTokenType>([.. _tokenTypes]),
        TokenModifiers = new Container<SemanticTokenModifier>([.. _tokenModifiers]),
    };

    // ── Construction ──────────────────────────────────────────────────────────
    public SemanticTokenService(
        IDocumentBufferService documentBufferService,
        IDeveroomLogger logger)
    {
        _documentBufferService = documentBufferService;
        _logger = logger;
    }

    // ── ISemanticTokenService ─────────────────────────────────────────────────
    public Task<SemanticTokens?> GetSemanticTokensAsync(
        DocumentUri uri, int version, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue((uri, version), out var tokens))
            return Task.FromResult<SemanticTokens?>(tokens);

        // Cache miss – encode from the tags already stored in the document buffer.
        if (!_documentBufferService.TryGet(uri, out var buffer) || buffer?.Tags is not { } tags || tags.Count == 0)
        {
            _logger.LogVerbose($"SemanticTokenService: no tags available for {uri} v{version}");
            return Task.FromResult<SemanticTokens?>(null);
        }

        var encoded = Encode(tags);
        tokens = new SemanticTokens { Data = [.. encoded] };
        _cache[(uri, version)] = tokens;
        PurgePriorVersions(uri, version);
        _logger.LogInfo($"SemanticTokenService: encoded {encoded.Count / 5} tokens for {uri} v{version}");
        return Task.FromResult<SemanticTokens?>(tokens);
    }

    // ── Encoding ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Converts a flat collection of <see cref="DeveroomTag"/> instances into the
    /// LSP semantic token integer encoding (5 ints per token):
    /// deltaLine, deltaStartChar, length, tokenTypeIndex, tokenModifierBitset.
    /// Only leaf-level tags that map to a token type are emitted; container
    /// block tags (FeatureBlock, etc.) are not emitted themselves but their
    /// children are processed recursively.
    /// </summary>
    private static List<int> Encode(IReadOnlyCollection<DeveroomTag> tags)
    {
        // Collect all leaf tokens in document order (line asc, char asc).
        var entries = new List<(int Line, int Char, int Length, TokenType Type, TokenModifier Modifiers)>();
        CollectLeafTokens(tags, entries);

        // Sort by (line, character) — tags should already be ordered but sort
        // defensively to guarantee the delta-encoding invariant.
        entries.Sort((a, b) =>
        {
            int c = a.Line.CompareTo(b.Line);
            return c != 0 ? c : a.Char.CompareTo(b.Char);
        });

        var result = new List<int>(entries.Count * 5);
        int prevLine = 0, prevChar = 0;

        foreach (var (line, ch, length, type, modifiers) in entries)
        {
            int deltaLine = line - prevLine;
            int deltaChar = deltaLine == 0 ? ch - prevChar : ch;

            result.Add(deltaLine);
            result.Add(deltaChar);
            result.Add(length);
            result.Add((int)type);
            result.Add((int)modifiers);

            prevLine = line;
            prevChar = ch;
        }

        return result;
    }

    private static void CollectLeafTokens(
        IEnumerable<DeveroomTag> tags,
        List<(int, int, int, TokenType, TokenModifier)> entries)
    {
        foreach (var tag in tags)
        {
            if (TryMapTag(tag, out var tokenType, out var modifiers))
            {
                var (startLine, startChar) = ResolvePosition(tag.Range, tag.Range.Start);
                var (endLine, endChar) = ResolvePosition(tag.Range, tag.Range.End);

                // For multi-line tokens emit one entry per line.
                if (startLine == endLine)
                {
                    int length = endChar - startChar;
                    if (length > 0)
                        entries.Add((startLine, startChar, length, tokenType, modifiers));
                }
                else
                {
                    // First line: from startChar to end of line
                    var firstLine = tag.Range.Snapshot.GetLineFromLineNumber(startLine);
                    int firstLineLength = firstLine.End - firstLine.Start - startChar;
                    if (firstLineLength > 0)
                        entries.Add((startLine, startChar, firstLineLength, tokenType, modifiers));

                    // Middle lines
                    for (int ln = startLine + 1; ln < endLine; ln++)
                    {
                        var midLine = tag.Range.Snapshot.GetLineFromLineNumber(ln);
                        int midLength = midLine.End - midLine.Start;
                        if (midLength > 0)
                            entries.Add((ln, 0, midLength, tokenType, modifiers));
                    }

                    // Last line: from column 0 to endChar
                    if (endChar > 0)
                        entries.Add((endLine, 0, endChar, tokenType, modifiers));
                }
            }

            // Do NOT recurse into ChildTags – the flat collection passed from
            // GherkinDocumentTaggerService already contains all descendants
            // (DeveroomTagParser.GetAllTags flattens the tree before caching).
        }
    }

    private static bool TryMapTag(
        DeveroomTag tag,
        out TokenType tokenType,
        out TokenModifier modifiers)
    {
        modifiers = TokenModifier.None;
        tokenType = default;

        switch (tag.Type)
        {
            case DeveroomTagTypes.DefinitionLineKeyword:
                tokenType = TokenType.Keyword;
                modifiers = TokenModifier.Declaration;
                return true;

            case DeveroomTagTypes.StepKeyword:
                tokenType = TokenType.Keyword;
                return true;

            case DeveroomTagTypes.Description:
            case DeveroomTagTypes.DocString:
                tokenType = TokenType.String;
                return true;

            case DeveroomTagTypes.StepParameter:
            case DeveroomTagTypes.ScenarioOutlinePlaceholder:
                tokenType = TokenType.Parameter;
                return true;

            case DeveroomTagTypes.Tag:
                tokenType = TokenType.Variable;
                return true;

            case DeveroomTagTypes.Comment:
                tokenType = TokenType.Comment;
                return true;

            case DeveroomTagTypes.DefinedStep:
                tokenType = TokenType.Function;
                return true;

            case DeveroomTagTypes.UndefinedStep:
                tokenType = TokenType.Regexp;
                modifiers = TokenModifier.Deprecated;
                return true;

            case DeveroomTagTypes.BindingError:
                tokenType = TokenType.Regexp;
                modifiers = TokenModifier.Deprecated;
                return true;

            case DeveroomTagTypes.DataTableHeader:
                tokenType = TokenType.Struct;
                modifiers = TokenModifier.Declaration;
                return true;

            // Block/container tags (FeatureBlock, RuleBlock, ScenarioDefinitionBlock,
            // ExamplesBlock, StepBlock, DataTable) and parse artifacts are skipped –
            // their child tags provide the precise, non-overlapping tokens.
            case DeveroomTagTypes.FeatureBlock:
            case DeveroomTagTypes.RuleBlock:
            case DeveroomTagTypes.ScenarioDefinitionBlock:
            case DeveroomTagTypes.ExamplesBlock:
            case DeveroomTagTypes.StepBlock:
            case DeveroomTagTypes.DataTable:
            case DeveroomTagTypes.Document:
            case DeveroomTagTypes.ScenarioHookReference:
            case DeveroomTagTypes.ParserError:
            default:
                return false;
        }
    }

    /// <summary>
    /// Resolves an absolute character offset within a snapshot to (line, character).
    /// </summary>
    private static (int Line, int Character) ResolvePosition(GherkinRange range, int absoluteOffset)
    {
        var snapshot = range.Snapshot;
        // Linear scan — acceptable for typical feature file sizes.
        for (int ln = 0; ln < snapshot.LineCount; ln++)
        {
            var line = snapshot.GetLineFromLineNumber(ln);
            if (absoluteOffset <= line.End)
                return (ln, absoluteOffset - line.Start);
        }
        // Clamp to end of last line.
        int lastLine = snapshot.LineCount - 1;
        var last = snapshot.GetLineFromLineNumber(lastLine);
        return (lastLine, last.End - last.Start);
    }

    // ── Cache housekeeping ────────────────────────────────────────────────────
    private void PurgePriorVersions(DocumentUri uri, int currentVersion)
    {
        foreach (var key in _cache.Keys)
        {
            if (key.Item1 == uri && key.Item2 < currentVersion)
                _cache.TryRemove(key, out _);
        }
    }
}