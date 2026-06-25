#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.DocumentOutline;
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;


using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Features.DocumentOutline;
using Reqnroll.IdeSupport.LSP.Server.Services;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Handlers;

public class FeatureDocumentSymbolHandlerTests
{
    private readonly IDocumentBufferService       _bufferService  = Substitute.For<IDocumentBufferService>();
    private readonly IGherkinDocumentSymbolService _symbolService  = Substitute.For<IGherkinDocumentSymbolService>();
    private readonly IDeveroomLogger               _logger         = Substitute.For<IDeveroomLogger>();

    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private FeatureDocumentSymbolHandler CreateSut() =>
        new(_bufferService, _symbolService, _logger);

    private static DocumentSymbolParams RequestFor(DocumentUri uri) =>
        new() { TextDocument = new TextDocumentIdentifier { Uri = uri } };

    private void SetupBuffer(DocumentUri uri, string text, IReadOnlyCollection<DeveroomTag>? tags)
    {
        var buf = new DocumentBuffer(uri, 1, text, tags);
        DocumentBuffer? outBuf;
        _bufferService.TryGet(uri, out outBuf)
            .Returns(x =>
            {
                x[1] = buf;
                return true;
            });
    }

    private static GherkinDocumentSymbol MakeSymbol(string name, GherkinSymbolKind kind)
    {
        var snap = new LspTextSnapshot(FeatureUri.ToString(), 1, "Feature: X\n");
        var range = new GherkinRange(snap, 0, 10);
        return new GherkinDocumentSymbol(name, null, kind, range, range,
            Array.Empty<GherkinDocumentSymbol>());
    }

    // ── Guard rails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_null_when_buffer_not_found_Async()
    {
        DocumentBuffer? ignored;
        _bufferService.TryGet(FeatureUri, out ignored).Returns(false);

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_tags_not_yet_computed_Async()
    {
        SetupBuffer(FeatureUri, "Feature: X\n", tags: null);

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_service_returns_no_symbols_Async()
    {
        SetupBuffer(FeatureUri, "Feature: X\n", tags: Array.Empty<DeveroomTag>());
        _symbolService.BuildSymbols(Arg.Any<IReadOnlyCollection<DeveroomTag>>())
                      .Returns(Array.Empty<GherkinDocumentSymbol>());

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeNull();
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_container_when_symbols_exist_Async()
    {
        var tags = Array.Empty<DeveroomTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("X", GherkinSymbolKind.Feature) });

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Top_level_symbol_has_correct_name_Async()
    {
        var tags = Array.Empty<DeveroomTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("MyFeature", GherkinSymbolKind.Feature) });

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        var first = result!.First();
        first.IsDocumentSymbol.Should().BeTrue();
        first.DocumentSymbol!.Name.Should().Be("MyFeature");
    }

    [Fact]
    public async Task Feature_symbol_maps_to_Module_kind_Async()
    {
        var tags = Array.Empty<DeveroomTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("X", GherkinSymbolKind.Feature) });

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result!.First().DocumentSymbol!.Kind.Should().Be(SymbolKind.Module);
    }

    [Fact]
    public async Task Scenario_symbol_maps_to_Method_kind_Async()
    {
        var tags = Array.Empty<DeveroomTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("X", GherkinSymbolKind.Scenario) });

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result!.First().DocumentSymbol!.Kind.Should().Be(SymbolKind.Method);
    }

    [Fact]
    public async Task Step_symbol_maps_to_Field_kind_Async()
    {
        var tags = Array.Empty<DeveroomTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("Given a step", GherkinSymbolKind.Step) });

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result!.First().DocumentSymbol!.Kind.Should().Be(SymbolKind.Field);
    }

    [Fact]
    public async Task Calls_symbol_service_with_buffer_tags_Async()
    {
        var tags = Array.Empty<DeveroomTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(Arg.Any<IReadOnlyCollection<DeveroomTag>>())
                      .Returns(Array.Empty<GherkinDocumentSymbol>());

        await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        _symbolService.Received(1).BuildSymbols(tags);
    }
}
