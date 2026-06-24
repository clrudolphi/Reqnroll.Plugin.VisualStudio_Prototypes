using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Formatting;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Parsing.GherkinDocuments;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Services;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

/// <summary>
/// Handles <c>textDocument/formatting</c> and <c>textDocument/rangeFormatting</c>
/// LSP requests for <c>.feature</c> files (F11 — Document Auto-formatting).
/// </summary>
public sealed class GherkinFormattingHandler
    : IDocumentFormattingHandler, IDocumentRangeFormattingHandler, IDocumentOnTypeFormattingHandler
{
    private readonly IDocumentBufferService _documentBufferService;
    private readonly IEditorConfigOptionsProvider _editorConfigOptionsProvider;
    private readonly IDeveroomConfigurationProvider _configurationProvider;
    private readonly IDeveroomLogger _logger;
    private readonly GherkinDocumentFormatter _formatter = new();

    private static readonly TextDocumentSelector FeatureSelector = new(
        new TextDocumentFilter { Pattern = "**/*.feature" });

    public GherkinFormattingHandler(
        IDocumentBufferService documentBufferService,
        IEditorConfigOptionsProvider editorConfigOptionsProvider,
        IDeveroomConfigurationProvider configurationProvider,
        IDeveroomLogger logger)
    {
        _documentBufferService = documentBufferService;
        _editorConfigOptionsProvider = editorConfigOptionsProvider;
        _configurationProvider = configurationProvider;
        _logger = logger;
    }

    // ── IDocumentFormattingHandler ────────────────────────────────────────────

    public DocumentFormattingRegistrationOptions GetRegistrationOptions(
        DocumentFormattingCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = FeatureSelector };

    public Task<TextEditContainer?> Handle(DocumentFormattingParams request, CancellationToken ct)
    {
        var filePath = request.TextDocument.Uri.GetFileSystemPath();
        _logger.LogInfo($"F11 textDocument/formatting: {request.TextDocument.Uri}");
        return FormatDocumentAsync(request.TextDocument.Uri, filePath, request.Options,
            startLine: null, endLine: null);
    }

    // ── IDocumentRangeFormattingHandler ──────────────────────────────────────

    public DocumentRangeFormattingRegistrationOptions GetRegistrationOptions(
        DocumentRangeFormattingCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = FeatureSelector };

    public async Task<TextEditContainer> Handle(DocumentRangeFormattingParams request, CancellationToken ct)
    {
        var filePath = request.TextDocument.Uri.GetFileSystemPath();
        _logger.LogInfo($"F11 textDocument/rangeFormatting: {request.TextDocument.Uri}");
        return await FormatDocumentAsync(
            request.TextDocument.Uri, filePath, request.Options,
            startLine: (int)request.Range.Start.Line,
            endLine: (int)request.Range.End.Line).ConfigureAwait(false)
            ?? new TextEditContainer();
    }

    // ── IDocumentOnTypeFormattingHandler ─────────────────────────────────────

    public DocumentOnTypeFormattingRegistrationOptions GetRegistrationOptions(
        DocumentOnTypeFormattingCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector      = FeatureSelector,
            FirstTriggerCharacter = "|",
            MoreTriggerCharacter  = new Container<string>("\n", "\t")
        };

    public Task<TextEditContainer?> Handle(DocumentOnTypeFormattingParams request, CancellationToken ct)
    {
        var filePath = request.TextDocument.Uri.GetFileSystemPath();
        _logger.LogInfo($"F12 textDocument/onTypeFormatting: trigger='{request.Character}' {request.TextDocument.Uri}");

        if (!_documentBufferService.TryGet(request.TextDocument.Uri, out var buffer) || buffer is null)
            return Task.FromResult<TextEditContainer?>(null);

        var text = buffer.Text;
        var lineEnding = DetectLineEnding(text);
        var allLines = SplitLines(text);
        var cursorLine = (int)request.Position.Line;

        var tableRange = GherkinDocumentFormatter.FindTableLineRange(allLines, cursorLine);
        if (tableRange is null)
            return Task.FromResult<TextEditContainer?>(null);

        var configuration = _configurationProvider.GetConfiguration();
        var formatSettings = GherkinFormatSettings.FromLspOptions(
            (int)request.Options.TabSize,
            request.Options.InsertSpaces,
            _editorConfigOptionsProvider,
            filePath ?? string.Empty,
            configuration);

        var gherkinDocument = ParseDocument(text);
        var tableNode = GherkinDocumentFormatter.FindTableAtLine(gherkinDocument, tableRange.Value.Start);
        if (tableNode is null)
            return Task.FromResult<TextEditContainer?>(null);

        // Preserve the actual indentation of the table rows rather than recomputing from AST levels.
        var firstRowLine = allLines[tableRange.Value.Start];
        var actualIndent = firstRowLine[..(firstRowLine.Length - firstRowLine.TrimStart().Length)];

        var (editStart, tableEnd) = tableRange.Value;
        editStart = Math.Max(0, Math.Min(editStart, allLines.Length - 1));
        tableEnd  = Math.Max(editStart, Math.Min(tableEnd, allLines.Length - 1));

        // For the \n trigger: cursor is on a blank line below the table.
        // Extend the edit range to include the cursor line so VS accepts the response —
        // some LSP clients reject onTypeFormatting edits that don't touch the cursor position.
        bool cursorBelowTable = tableEnd < cursorLine && cursorLine < allLines.Length;
        int editEnd = cursorBelowTable ? Math.Min(cursorLine, allLines.Length - 1) : tableEnd;

        // Capture original end-line length BEFORE the formatter mutates allLines in-place.
        // DocumentLinesEditBuffer.SetLine writes back to the same array, so querying
        // allLines[editEnd].Length after FormatTable returns the formatted (wrong) length.
        var originalEditEndLineLength = allLines[editEnd].Length;

        var linesBuffer = new DocumentLinesEditBuffer(allLines, editStart, tableEnd);
        _formatter.FormatTable(linesBuffer, tableNode, formatSettings, actualIndent);

        var formattedLines = linesBuffer.GetEditedLines();
        // When cursor is below the table the edit range ends at (cursorLine, 0).
        // The trailing line ending ensures the replacement covers lines editStart-(tableEnd)
        // plus their terminators, leaving the blank cursor line intact.
        var formattedText = cursorBelowTable
            ? string.Join(lineEnding, formattedLines) + lineEnding
            : string.Join(lineEnding, formattedLines);

        var editRange = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
            new Position(editStart, 0),
            new Position(editEnd, originalEditEndLineLength));

        return Task.FromResult<TextEditContainer?>(new TextEditContainer(
            new TextEdit { Range = editRange, NewText = formattedText }));
    }

    // ── Shared implementation ─────────────────────────────────────────────────

    private Task<TextEditContainer?> FormatDocumentAsync(
        OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri uri,
        string? filePath,
        FormattingOptions lspOptions,
        int? startLine,
        int? endLine)
    {
        if (!_documentBufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogWarning($"Formatting requested for unknown document: {uri}");
            return Task.FromResult<TextEditContainer?>(null);
        }

        var text = buffer.Text;
        var lineEnding = DetectLineEnding(text);
        var allLines = SplitLines(text);

        if (allLines.Length == 0)
            return Task.FromResult<TextEditContainer?>(null);

        var configuration = _configurationProvider.GetConfiguration();
        var formatSettings = GherkinFormatSettings.FromLspOptions(
            (int)lspOptions.TabSize,
            lspOptions.InsertSpaces,
            _editorConfigOptionsProvider,
            filePath ?? string.Empty,
            configuration);

        var gherkinDocument = ParseDocument(text);
        if (gherkinDocument?.Feature == null)
            return Task.FromResult<TextEditContainer?>(null);

        var editStart = startLine ?? 0;
        var editEnd = endLine ?? (allLines.Length - 1);

        // Clamp range to valid bounds
        editStart = Math.Max(0, Math.Min(editStart, allLines.Length - 1));
        editEnd = Math.Max(editStart, Math.Min(editEnd, allLines.Length - 1));

        // Capture original end-line length BEFORE the formatter mutates allLines in-place.
        var originalEditEndLineLength = allLines[editEnd].Length;

        var linesBuffer = new DocumentLinesEditBuffer(allLines, editStart, editEnd);
        _formatter.FormatGherkinDocument(gherkinDocument, linesBuffer, formatSettings);

        var formattedLines = linesBuffer.GetEditedLines();
        var formattedText = string.Join(lineEnding, formattedLines);

        var editRange = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
            new Position(editStart, 0),
            new Position(editEnd, originalEditEndLineLength));

        return Task.FromResult<TextEditContainer?>(new TextEditContainer(
            new TextEdit { Range = editRange, NewText = formattedText }));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DeveroomGherkinDocument ParseDocument(string text)
    {
        var language = _configurationProvider.GetConfiguration().DefaultFeatureLanguage ?? "en";
        var dialectProvider = ReqnrollGherkinDialectProvider.Get(language);
        var parser = new DeveroomGherkinParser(dialectProvider, NullMonitoringService.Instance);
        parser.ParseAndCollectErrors(text, _logger, out var gherkinDocument, out _);
        return gherkinDocument;
    }

    private static string DetectLineEnding(string text)
    {
        if (text.Contains("\r\n")) return "\r\n";
        if (text.Contains("\r"))   return "\r";
        return "\n";
    }

    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
    }
}
