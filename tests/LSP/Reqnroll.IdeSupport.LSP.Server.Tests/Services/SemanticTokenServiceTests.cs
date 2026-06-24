using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Classification;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Parsing.GherkinDocuments;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;

using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

using Reqnroll.IdeSupport.LSP.Server.Services;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Services;

public class SemanticTokenServiceTests
{
    private readonly IDocumentBufferService _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly IDeveroomLogger _logger = Substitute.For<IDeveroomLogger>();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private SemanticTokenService CreateSut() => new(_bufferService, _logger);

    private void SetupBuffer(DocumentBuffer? buf)
    {
        DocumentBuffer? ignored;
        _bufferService.TryGet(FeatureUri, out ignored).Returns(x =>
        {
            x[1] = buf;
            return buf is not null;
        });
    }

    // ── Legend ────────────────────────────────────────────────────────────────

    [Fact]
    public void Legend_is_not_null()
    {
        var sut = CreateSut();
        sut.Legend.Should().NotBeNull();
    }

    [Fact]
    public void Legend_contains_the_custom_reqnroll_token_types()
    {
        var sut = CreateSut();
        var advertised = sut.Legend.TokenTypes.Select(t => t.ToString()).ToList();
        advertised.Should().Contain(ReqnrollClassificationTypeNames.Keyword);
        advertised.Should().Contain(ReqnrollClassificationTypeNames.StepParameter);
        advertised.Should().Contain(ReqnrollClassificationTypeNames.UndefinedStep);
    }

    [Fact]
    public void Legend_declares_no_token_modifiers()
    {
        var sut = CreateSut();
        sut.Legend.TokenModifiers.Should().BeEmpty();
    }

    // ── No buffer / no tags ───────────────────────────────────────────────────

    [Fact]
    public async Task GetSemanticTokensAsync_returns_null_when_buffer_not_registered()
    {
        SetupBuffer(null);
        var sut = CreateSut();
        var result = await sut.GetSemanticTokensAsync(FeatureUri, 1);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSemanticTokensAsync_returns_null_when_buffer_has_no_tags()
    {
        var buf = new DocumentBuffer(FeatureUri, 1, "Feature: X\n"); // Tags is null/empty
        SetupBuffer(buf);
        var sut = CreateSut();
        var result = await sut.GetSemanticTokensAsync(FeatureUri, 1);
        result.Should().BeNull();
    }

    // ── With tags ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSemanticTokensAsync_returns_non_null_when_tags_available()
    {
        // Build a buffer with at least one tag that maps to a token type.
        // We need a real DeveroomTag with a valid GherkinRange; use a minimal
        // stub snapshot so the range/position calculation works.
        var snapshot = new TestGherkinSnapshot("Feature: Test\n  Scenario: S\n    Given something\n");
        var range = new GherkinRange(snapshot, 0, snapshot.Length);
        var tag = new DeveroomTag(DeveroomTagTypes.DefinitionLineKeyword, range);

        var buf = new DocumentBuffer(FeatureUri, 2, snapshot.GetText());
        buf = buf with { Tags = new[] { tag } };
        SetupBuffer(buf);

        var sut = CreateSut();
        var result = await sut.GetSemanticTokensAsync(FeatureUri, 2);
        result.Should().NotBeNull();
        result!.Data.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetSemanticTokensAsync_returns_cached_result_on_second_call()
    {
        var snapshot = new TestGherkinSnapshot("Feature: Test\n");
        var range = new GherkinRange(snapshot, 0, 7);
        var tag = new DeveroomTag(DeveroomTagTypes.StepKeyword, range);

        var buf = new DocumentBuffer(FeatureUri, 3, snapshot.GetText());
        buf = buf with { Tags = new[] { tag } };
        SetupBuffer(buf);

        var sut = CreateSut();
        var first = await sut.GetSemanticTokensAsync(FeatureUri, 3);
        var second = await sut.GetSemanticTokensAsync(FeatureUri, 3);

        second.Should().BeSameAs(first);
        // Buffer service should only be called once (second call hits cache)
        _bufferService.Received(1).TryGet(FeatureUri, out Arg.Any<DocumentBuffer?>());
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>Minimal IGherkinTextSnapshot backed by a plain string.</summary>
    private sealed class TestGherkinSnapshot : Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot
    {
        private readonly string _text;
        private readonly string[] _lines;

        public TestGherkinSnapshot(string text)
        {
            _text = text;
            // Split preserving line content; last empty segment from trailing \n is dropped.
            _lines = text.Split('\n');
        }

        public int Version => 1;
        public int Length => _text.Length;
        public int LineCount => _lines.Length;
        public string GetText() => _text;

        public Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshotLine GetLineFromLineNumber(int lineNumber)
            => new Line(_lines, lineNumber);

        private sealed class Line : Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshotLine
        {
            private readonly string[] _lines;
            private readonly int _lineNumber;
            private readonly int _start;

            public Line(string[] lines, int lineNumber)
            {
                _lines = lines;
                _lineNumber = lineNumber;
                int s = 0;
                for (int i = 0; i < lineNumber; i++)
                    s += lines[i].Length + 1; // +1 for \n
                _start = s;
            }

            public int LineNumber => _lineNumber;
            public int Start => _start;
            public int End => _start + _lines[_lineNumber].Length;
            public string GetText() => _lines[_lineNumber];
        }
    }
}
