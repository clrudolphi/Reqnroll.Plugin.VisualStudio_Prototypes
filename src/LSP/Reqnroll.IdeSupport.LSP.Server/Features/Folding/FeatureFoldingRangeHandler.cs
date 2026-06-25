using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Folding;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;


using Reqnroll.IdeSupport.LSP.Server.Services;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Folding;

/// <summary>
/// Handles <c>textDocument/foldingRange</c> for <c>.feature</c> files (F10 — Code Folding).
/// Returns foldable regions for Feature bodies, Scenario/Background/Rule blocks,
/// Doc strings, Data tables, and Examples blocks.
/// </summary>
public sealed class FeatureFoldingRangeHandler : IFoldingRangeHandler
{
    private readonly IDocumentBufferService        _documentBufferService;
    private readonly IGherkinFoldingRangeService    _foldingService;
    private readonly IDeveroomLogger               _logger;

    private static readonly TextDocumentSelector FeatureSelector = new(
        new TextDocumentFilter { Pattern = "**/*.feature" });

    public FeatureFoldingRangeHandler(
        IDocumentBufferService documentBufferService,
        IGherkinFoldingRangeService foldingService,
        IDeveroomLogger logger)
    {
        _documentBufferService = documentBufferService;
        _foldingService         = foldingService;
        _logger                = logger;
    }

    public FoldingRangeRegistrationOptions GetRegistrationOptions(
        FoldingRangeCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = FeatureSelector };

    public Task<Container<FoldingRange>?> Handle(
        FoldingRangeRequestParam request, CancellationToken ct)
    {
        _logger.LogInfo($"F10 textDocument/foldingRange: {request.TextDocument.Uri}");

        if (!_documentBufferService.TryGet(request.TextDocument.Uri, out var buffer) || buffer?.Tags is null)
            return Task.FromResult<Container<FoldingRange>?>(null);

        var ranges = _foldingService.BuildFoldingRanges(buffer.Tags);
        if (ranges.Count == 0)
            return Task.FromResult<Container<FoldingRange>?>(new Container<FoldingRange>());

        var container = new Container<FoldingRange>(
            ranges.Select(ToFoldingRange));

        return Task.FromResult<Container<FoldingRange>?>(container);
    }

    // ── Conversion helpers ────────────────────────────────────────────────

    private static FoldingRange ToFoldingRange(GherkinFoldingRange r)
    {
        if (r.Kind.HasValue)
        {
            var lspKind = r.Kind.Value switch
            {
                GherkinFoldingRangeKind.Comment => FoldingRangeKind.Comment,
                GherkinFoldingRangeKind.Imports => FoldingRangeKind.Imports,
                GherkinFoldingRangeKind.Region  => FoldingRangeKind.Region,
                _                               => (FoldingRangeKind?)null,
            };
            return new FoldingRange
            {
                StartLine = r.StartLine,
                EndLine   = r.EndLine,
                Kind      = lspKind,
            };
        }

        return new FoldingRange
        {
            StartLine = r.StartLine,
            EndLine   = r.EndLine,
        };
    }
}
