#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

/// <summary>
/// Handles the custom <c>reqnroll/findStepUsages</c> request (F14 P2b).
/// <para>
/// Implements MediatR IRequestHandler to allow automatic routing via AddMediatR,
/// avoiding the need for manual OnRequest delegate registration and IServiceProvider capture.
/// Implements the full three-state contract that <c>textDocument/references</c> cannot carry:
/// <list type="bullet">
///   <item>Returns <see langword="null"/> when the caret is not on any step-definition binding
///         (client falls through to the built-in C# Find All References).</item>
///   <item>Returns a response with <c>isBinding=true</c> and an empty location list when the
///         binding has no matching feature steps ("0 usages" window).</item>
///   <item>Returns a response with <c>isBinding=true</c> and populated locations otherwise.</item>
/// </list>
/// </para>
/// <para>
/// Each location includes a <c>stepText</c> field extracted directly from the document snapshot
/// stored in the match cache at parse time — the client does not need to read the file from disk.
/// </para>
/// </summary>
/// <remarks>
/// Shares the same dependency set as <see cref="StepReferencesHandler"/>;
/// both are registered in <c>Program.cs</c> and resolved as singletons from the DI container.
/// </remarks>
public sealed class FindStepUsagesHandler
{
    private readonly IBindingMatchService         _matchService;
    private readonly ILspWorkspaceScopeManager    _scopeManager;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IDeveroomLogger               _logger;

    public FindStepUsagesHandler(
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

    /// <summary>
    /// Handles a <c>reqnroll/findStepUsages</c> request.
    /// Request params are identical to <c>textDocument/references</c> (<see cref="ReferenceParams"/>).
    /// </summary>
    public Task<FindStepUsagesResponse?> HandleAsync(
        ReferenceParams   request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        if (!IsCSharp(uri))
        {
            _logger.LogVerbose($"FindStepUsagesHandler: ignoring non-.cs URI {uri}");
            return Task.FromResult<FindStepUsagesResponse?>(null);
        }

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return Task.FromResult<FindStepUsagesResponse?>(null);

        // LSP positions are 0-based; SourceLocation is 1-based.
        var line   = request.Position.Line + 1;
        var column = request.Position.Character + 1;
        var bindingLocation = new SourceLocation(filePath, line, column);

        // Q18 2B: restrict search to the projects that own this .cs file.
        var owners = _scopeManager.ResolveOwners(uri);
        IReadOnlyCollection<ProjectOwner>? projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker))
                    .ToArray()
            : null;

        var usages = _matchService.FindUsages(bindingLocation, projectFilter);

        if (usages.Count == 0)
        {
            // P1 three-state: distinguish "not a binding" (→ null) from "binding, 0 usages" (→ empty list).
            var hasBinding = _registryLookup.HasBindingAtLocation(uri, bindingLocation);
            if (!hasBinding)
            {
                _logger.LogVerbose(
                    $"FindStepUsagesHandler: no binding at {filePath}:{line} — returning isBinding=false (fall through)");
                // Return isBinding=false rather than null: OmniSharp's OnRequest framework does not
                // serialise null gracefully for custom response types (sends an error response instead).
                // The VS client checks IsBinding and treats false as "not a binding".
                return Task.FromResult<FindStepUsagesResponse?>(new FindStepUsagesResponse { IsBinding = false });
            }

            _logger.LogVerbose(
                $"FindStepUsagesHandler: binding at {filePath}:{line} has 0 usages");
            return Task.FromResult<FindStepUsagesResponse?>(
                new FindStepUsagesResponse { IsBinding = true });
        }

        _logger.LogVerbose(
            $"FindStepUsagesHandler: {usages.Count} usage(s) for binding at {filePath}:{line}");

        var items = usages
            .Select(match => ToItem(match))
            .ToList();

        return Task.FromResult<FindStepUsagesResponse?>(
            new FindStepUsagesResponse { IsBinding = true, Locations = items });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FindStepUsageItem ToItem(StepBindingMatch match)
    {
        var (startLine, startChar) = match.Range.StartLinePosition;
        var (endLine,   endChar)   = match.Range.EndLinePosition;

        var item = new FindStepUsageItem
        {
            Uri         = match.FeatureDocumentId,
            StartLine   = startLine,
            StartChar   = startChar,
            EndLine     = endLine,
            EndChar     = endChar,
            Keyword     = match.Keyword,
            ScenarioName = match.ScenarioName,
            ProjectName = match.ProjectName,
        };

        // Extract the step text directly from the snapshot stored in the match cache.
        // The GherkinRange carries the snapshot from the most recent parse of the feature file,
        // so no disk I/O is needed and the text is consistent with the cached match.
        try
        {
            var snapshotText = match.Range.Snapshot.GetText();
            if (match.Range.Start >= 0 && match.Range.End <= snapshotText.Length)
            {
                var raw = snapshotText.Substring(match.Range.Start, match.Range.Length).Trim();
                if (raw.Length > 0)
                    item.StepText = raw;
            }
        }
        catch (Exception)
        {
            // Best-effort: leave StepText null; the client falls back to its disk-read path.
        }

        return item;
    }

    private static bool IsCSharp(DocumentUri uri) =>
        uri.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
