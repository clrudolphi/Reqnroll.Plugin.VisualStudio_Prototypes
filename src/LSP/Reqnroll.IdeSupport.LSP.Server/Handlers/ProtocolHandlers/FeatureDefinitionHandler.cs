#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Server.Services;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

/// <summary>
/// Handles <c>textDocument/definition</c> requests originating from a cursor position in a
/// <c>.feature</c> file (design doc F5 — Go to Step Definition).
/// <para>
/// Implements <see cref="IDefinitionHandler"/> so OmniSharp registers the capability via
/// <c>client/registerCapability</c> (dynamic registration) after the handshake, scoped to
/// <c>**/*.feature</c> files only.
/// </para>
/// </summary>
public sealed class FeatureDefinitionHandler : IDefinitionHandler
{
    private readonly IBindingMatchService      _matchService;
    private readonly IDocumentBufferService    _bufferService;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IDeveroomLogger           _logger;

    public FeatureDefinitionHandler(
        IBindingMatchService      matchService,
        IDocumentBufferService    bufferService,
        ILspWorkspaceScopeManager scopeManager,
        IDeveroomLogger           logger)
    {
        _matchService  = matchService;
        _bufferService = bufferService;
        _scopeManager  = scopeManager;
        _logger        = logger;
    }

    public DefinitionRegistrationOptions GetRegistrationOptions(
        DefinitionCapability    capability,
        ClientCapabilities      clientCapabilities)
        => new()
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.feature" })
        };

    public Task<LocationOrLocationLinks?> Handle(
        DefinitionParams  request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"FeatureDefinitionHandler: ignoring non-.feature URI {uri}");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"FeatureDefinitionHandler: no document buffer for {uri}");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        var snapshot = buffer.ToGherkinTextSnapshot();
        var offset   = snapshot.ToOffset(request.Position.Line, request.Position.Character);

        // Resolve the primary owner; fall back to Unknown for pre-baseline startup.
        var primaryOwner = _scopeManager.ResolvePrimaryOwner(uri);
        var owner = primaryOwner is not null
            ? new ProjectOwner(primaryOwner.ProjectFullName, primaryOwner.TargetFrameworkMoniker)
            : ProjectOwner.Unknown;

        var docId = uri.ToString();
        if (!_matchService.TryGet(new MatchSetKey(docId, owner), out var matchSet) || matchSet is null)
        {
            _logger.LogVerbose($"FeatureDefinitionHandler: no match set cached for {uri}");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        var step = matchSet.FindAt(offset);
        if (step is null)
        {
            _logger.LogVerbose($"FeatureDefinitionHandler: no step at offset {offset} in {uri}");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        var locations = step.Result.Items
            .Select(item => item.MatchedStepDefinition?.Implementation)
            .Where(impl => impl?.SourceLocation?.SourceFile is not (null or ""))
            .Select(impl => impl!.SourceLocation!.WithIdentifierLocation(impl.Method))
            .Select(loc => new LocationOrLocationLink(loc.ToLspLocation()))
            .ToArray();

        if (locations.Length == 0)
        {
            _logger.LogVerbose(
                $"FeatureDefinitionHandler: step at offset {offset} in {uri} has no binding locations (undefined/ambiguous)");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        _logger.LogVerbose(
            $"FeatureDefinitionHandler: {locations.Length} location(s) for step at offset {offset} in {uri}");

        return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(locations));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);
}
