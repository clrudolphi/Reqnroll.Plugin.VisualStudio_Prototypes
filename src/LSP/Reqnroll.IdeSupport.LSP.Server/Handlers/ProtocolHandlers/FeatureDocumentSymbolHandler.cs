using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.DocumentOutline;
using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Services;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

/// <summary>
/// Handles <c>textDocument/documentSymbol</c> for <c>.feature</c> files (F9 — Document Outline).
/// Returns a nested <see cref="DocumentSymbol"/> hierarchy:
/// Feature → Background / Rule → Scenario / Scenario Outline → Step / Examples.
/// </summary>
public sealed class FeatureDocumentSymbolHandler : IDocumentSymbolHandler
{
    private readonly IDocumentBufferService        _documentBufferService;
    private readonly IGherkinDocumentSymbolService _symbolService;
    private readonly IDeveroomLogger               _logger;

    private static readonly TextDocumentSelector FeatureSelector = new(
        new TextDocumentFilter { Pattern = "**/*.feature" });

    public FeatureDocumentSymbolHandler(
        IDocumentBufferService documentBufferService,
        IGherkinDocumentSymbolService symbolService,
        IDeveroomLogger logger)
    {
        _documentBufferService = documentBufferService;
        _symbolService         = symbolService;
        _logger                = logger;
    }

    public DocumentSymbolRegistrationOptions GetRegistrationOptions(
        DocumentSymbolCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = FeatureSelector };

    public Task<SymbolInformationOrDocumentSymbolContainer?> Handle(
        DocumentSymbolParams request, CancellationToken ct)
    {
        _logger.LogInfo($"F9 textDocument/documentSymbol: {request.TextDocument.Uri}");

        if (!_documentBufferService.TryGet(request.TextDocument.Uri, out var buffer) || buffer?.Tags is null)
            return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(null);

        var symbols = _symbolService.BuildSymbols(buffer.Tags);
        if (symbols.Count == 0)
            return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(null);

        var container = new SymbolInformationOrDocumentSymbolContainer(
            symbols.Select(s => SymbolInformationOrDocumentSymbol.Create(ToDocumentSymbol(s))));

        return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(container);
    }

    // ── Conversion helpers ────────────────────────────────────────────────────

    private static DocumentSymbol ToDocumentSymbol(GherkinDocumentSymbol s)
    {
        var children = s.Children.Count > 0
            ? new Container<DocumentSymbol>(s.Children.Select(ToDocumentSymbol))
            : null;

        return new DocumentSymbol
        {
            Name           = s.Name,
            Detail         = s.Detail,
            Kind           = ToSymbolKind(s.Kind),
            Range          = s.Range.ToLspRange(),
            SelectionRange = s.SelectionRange.ToLspRange(),
            Children       = children,
        };
    }

    private static SymbolKind ToSymbolKind(GherkinSymbolKind kind) => kind switch
    {
        GherkinSymbolKind.Feature        => SymbolKind.Module,
        GherkinSymbolKind.Background     => SymbolKind.Constructor,
        GherkinSymbolKind.Rule           => SymbolKind.Namespace,
        GherkinSymbolKind.Scenario       => SymbolKind.Method,
        GherkinSymbolKind.ScenarioOutline => SymbolKind.Method,
        GherkinSymbolKind.Step           => SymbolKind.Field,
        GherkinSymbolKind.Examples       => SymbolKind.Array,
        _                                => SymbolKind.Object,
    };
}
