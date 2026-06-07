using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery;

/// <summary>
/// Drives Roslyn-based (source-level) binding discovery (design doc feature F2) when a
/// <c>.cs</c> step-definition file is opened or edited.  Resolves all projects that own the
/// document via the membership index (<see cref="ILspWorkspaceScopeManager.ResolveOwners"/>),
/// parses the supplied source text with <see cref="StepDefinitionFileParser"/> (via
/// <see cref="ProjectBindingRegistry.ReplaceBindings"/>), and patches each owning project's
/// <see cref="ConnectorBindingRegistryProvider"/> so the change is reflected immediately
/// without waiting for a build / connector run.
/// </summary>
/// <remarks>
/// Invariant I2 — if the membership index has received a baseline and the file is not in it,
/// the file is <em>excluded</em> from all registries and this method is a no-op for it.
/// This prevents phantom bindings from open-but-excluded files.
/// </remarks>
public sealed class CSharpBindingDiscoveryService : ICSharpBindingDiscoveryService
{
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IDeveroomLogger _logger;

    public CSharpBindingDiscoveryService(ILspWorkspaceScopeManager scopeManager, IDeveroomLogger logger)
    {
        _scopeManager = scopeManager;
        _logger = logger;
    }

    public async Task UpdateFromSourceAsync(DocumentUri uri, string text, CancellationToken cancellationToken)
    {
        var owners = _scopeManager.ResolveOwners(uri);

        if (owners.Count == 0)
        {
            var state = _scopeManager.GetMembershipState(uri);
            if (state == MembershipState.Unowned)
                _logger.LogVerbose(
                    $"[Roslyn] '{uri}' is excluded from all projects (I2); skipping source-level discovery.");
            else
                _logger.LogVerbose(
                    $"[Roslyn] No project owns '{uri}' (state={state}); skipping source-level discovery.");
            return;
        }

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return;

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var project in owners)
        {
            if (!project.Properties.TryGetValue(typeof(ConnectorBindingRegistryProvider), out var obj)
                || obj is not ConnectorBindingRegistryProvider provider)
            {
                _logger.LogVerbose(
                    $"[Roslyn] Project '{project.ProjectName}' has no binding provider yet; skipping.");
                continue;
            }

            var previousCount = provider.Current.StepDefinitions.Length;
            var file = FileDetails.FromPath(filePath).WithCSharpContent(text);
            await provider.ApplyRoslynFileUpdateAsync(file).ConfigureAwait(false);
            var newCount = provider.Current.StepDefinitions.Length;
            var delta = newCount - previousCount;
            var deltaStr = delta == 0 ? "no change" : (delta > 0 ? $"+{delta}" : delta.ToString());

            _logger.LogInfo(
                $"[Roslyn] Re-discovered bindings for '{Path.GetFileName(filePath)}' " +
                $"in project '{project.ProjectName}': {newCount} step definition(s) ({deltaStr}).");
        }
    }
}
