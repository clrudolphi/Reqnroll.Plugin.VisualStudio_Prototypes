using MediatR;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Commenting;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Services;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

/// <summary>
/// Handles <c>workspace/executeCommand</c> for <c>reqnroll.toggleComment</c> (F13 — Comment/Uncomment).
/// Toggles <c>#</c> comments on the selected line(s) of a <c>.feature</c> file.
/// Arguments: <c>[uri, startLine, endLine]</c> (0-based, inclusive).
/// Applies the resulting <see cref="WorkspaceEdit"/> via <c>workspace/applyEdit</c> notification.
/// </summary>
public sealed class CommentToggleHandler : IExecuteCommandHandler
{
    private const string ToggleCommentCommand = "reqnroll.toggleComment";

    private readonly IDocumentBufferService   _documentBufferService;
    private readonly ICommentToggleService     _toggleService;
    private readonly ILanguageServerFacade     _languageServer;
    private readonly IDeveroomLogger           _logger;
    private readonly ILspTelemetryService?     _telemetryService;

    public CommentToggleHandler(
        IDocumentBufferService documentBufferService,
        ICommentToggleService toggleService,
        ILanguageServerFacade languageServer,
        IDeveroomLogger logger,
        ILspTelemetryService? telemetryService = null)
    {
        _documentBufferService = documentBufferService;
        _toggleService         = toggleService;
        _languageServer        = languageServer;
        _logger                = logger;
        _telemetryService      = telemetryService;
    }

    public ExecuteCommandRegistrationOptions GetRegistrationOptions(
        ExecuteCommandCapability capability,
        ClientCapabilities clientCapabilities)
        => new()
        {
            Commands = new Container<string>(ToggleCommentCommand)
        };

    public Task<Unit> Handle(
        ExecuteCommandParams request,
        CancellationToken cancellationToken)
    {
        if (request.Command != ToggleCommentCommand)
        {
            _logger.LogVerbose($"CommentToggleHandler: unknown command '{request.Command}'");
            return Task.FromResult(Unit.Value);
        }

        var args = request.Arguments;
        if (args is null || args.Count < 3)
        {
            _logger.LogVerbose("CommentToggleHandler: missing arguments");
            return Task.FromResult(Unit.Value);
        }

        var uriStr    = args[0].Value<string>();
        var startLine = args[1].Value<int>();
        var endLine   = args[2].Value<int>();

        if (uriStr is null)
        {
            _logger.LogVerbose("CommentToggleHandler: null URI argument");
            return Task.FromResult(Unit.Value);
        }

        var uri = DocumentUri.Parse(uriStr);

        if (!_documentBufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"CommentToggleHandler: buffer not found for {uri}");
            return Task.FromResult(Unit.Value);
        }

        var text   = buffer.Text;
        var lines  = text.Replace("\r\n", "\n").Split('\n');
        var result = _toggleService.ToggleComment(text, startLine, endLine);

        var edit = BuildWorkspaceEdit(uri, result, lines);
        _logger.LogInfo($"F13 reqnroll.toggleComment: {uri} lines [{startLine}..{endLine}] → {result.Edits.Count} change(s)");

        // Telemetry
        _telemetryService?.SendEvent("CommentUncomment command executed", new());

        _languageServer.SendNotification(LspMethodNames.WorkspaceApplyEdit, edit);

        return Task.FromResult(Unit.Value);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ApplyWorkspaceEditParams BuildWorkspaceEdit(
        DocumentUri uri,
        GherkinCommentToggleResult result,
        string[] lines)
    {
        var textEdits = new TextEditContainer(
            result.Edits.Select(e => new TextEdit
            {
                // End character = line length so the range covers the full line content
                // (not the newline), turning this into a replacement rather than an insertion.
                Range   = new LspRange(
                    new Position(e.StartLine, 0),
                    new Position(e.EndLine, e.EndLine < lines.Length ? lines[e.EndLine].Length : 0)),
                NewText = e.NewText
            }));

        return new ApplyWorkspaceEditParams
        {
            Edit = new WorkspaceEdit
            {
                DocumentChanges = new Container<WorkspaceEditDocumentChange>(
                    new WorkspaceEditDocumentChange(new TextDocumentEdit
                    {
                        TextDocument = new OptionalVersionedTextDocumentIdentifier
                        {
                            Uri     = uri,
                            Version = null
                        },
                        Edits = textEdits
                    }))
            }
        };
    }
}
