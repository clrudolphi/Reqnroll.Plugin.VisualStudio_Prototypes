#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

/// <summary>
/// Handles <c>textDocument/references</c> requests originating from a cursor position in a
/// <c>.cs</c> binding file (design doc F14 — Find Step Definition Usages).
/// <para>
/// Converts the cursor position to a <see cref="SourceLocation"/> (file path + 1-based line),
/// queries the binding match cache for every feature-file step that resolves to that location,
/// and returns the results as an array of LSP <see cref="Location"/> objects.
/// </para>
/// </summary>
/// <remarks>
/// Q18 2B: the scope is restricted to the projects that own the queried <c>.cs</c> file.
/// This prevents cross-project bleed when two projects have step definitions at the same
/// source location (same file name + line in a shared binding class).
/// </remarks>
public sealed class StepReferencesHandler
{
    private readonly IBindingMatchService         _matchService;
    private readonly ILspWorkspaceScopeManager    _scopeManager;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IDeveroomLogger               _logger;

    public StepReferencesHandler(
        IBindingMatchService          matchService,
        ILspWorkspaceScopeManager     scopeManager,
        IProjectBindingRegistryLookup registryLookup,
        IDeveroomLogger               logger)
    {
        _matchService   = matchService;
        _scopeManager   = scopeManager;
        _registryLookup = registryLookup;
        _logger         = logger;
    }

    public Task<LocationOrLocationLinks?> HandleAsync(
        ReferenceParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        if (!IsCSharp(uri))
        {
            _logger.LogVerbose($"StepReferencesHandler: ignoring non-.cs URI {uri}");
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return Task.FromResult<LocationOrLocationLinks?>(null);

        // LSP positions are 0-based; SourceLocation is 1-based.
        var line   = request.Position.Line + 1;
        var column = request.Position.Character + 1;
        var bindingLocation = new SourceLocation(filePath, line, column);

        // Q18 2B: restrict search to the projects that own this .cs file.
        // ResolveOwners returns an empty list only when no project claims the file; in that
        // case pass null to FindUsages so it searches all cached match sets (backward compat).
        var owners = _scopeManager.ResolveOwners(uri);
        IReadOnlyCollection<ProjectOwner>? projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker))
                    .ToArray()
            : null;

        var usages = _matchService.FindUsages(bindingLocation, projectFilter);

        if (usages.Count == 0)
        {
            // P1: distinguish "not a binding at this location" from "binding with 0 matching steps".
            // HasBindingAtLocation checks the per-project registries for any binding spanning the
            // query line. The three-state contract (null/empty/locations) is the correct design,
            // but OmniSharp's LocationOrLocationLinks JSON converter does not support null
            // serialization, so both "not a binding" and "0 usages" return an empty response over
            // textDocument/references. The VS client (P2) will use a custom reqnroll/findStepUsages
            // request that can carry the full three-state result.
            var hasBinding = _registryLookup.HasBindingAtLocation(uri, bindingLocation);
            if (!hasBinding)
                _logger.LogVerbose(
                    $"StepReferencesHandler: no binding at {filePath}:{line}");
            else
                _logger.LogVerbose(
                    $"StepReferencesHandler: binding at {filePath}:{line} has 0 usages");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        _logger.LogVerbose(
            $"StepReferencesHandler: {usages.Count} usage(s) for binding at {filePath}:{line}");

        var locations = usages
            .Select(match => new LocationOrLocationLink(new Location
            {
                Uri   = DocumentUri.Parse(match.FeatureDocumentId),
                Range = match.Range.ToLspRange()
            }))
            .ToArray();

        return Task.FromResult<LocationOrLocationLinks?>(
            new LocationOrLocationLinks(locations));
    }

    private static bool IsCSharp(DocumentUri uri) =>
        uri.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
