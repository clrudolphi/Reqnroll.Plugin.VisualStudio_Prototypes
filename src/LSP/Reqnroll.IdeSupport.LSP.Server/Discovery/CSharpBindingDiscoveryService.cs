using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery;

/// <summary>
/// Drives Roslyn-based (source-level) binding discovery (design doc feature F2) when a
/// <c>.cs</c> step-definition file is opened or edited. Resolves the project that owns the
/// document, parses the supplied source text with <see cref="StepDefinitionFileParser"/> (via
/// <see cref="ProjectBindingRegistry.ReplaceBindings"/>), and patches that project's
/// <see cref="ConnectorBindingRegistryProvider"/> so the change is reflected immediately —
/// without waiting for a build / connector run.
/// </summary>
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
        var project = _scopeManager.GetProjectForUri(uri);
        if (project is null)
        {
            _logger.LogVerbose($"[Roslyn] No project owns '{uri}'; skipping source-level discovery.");
            return;
        }

        if (!project.Properties.TryGetValue(typeof(ConnectorBindingRegistryProvider), out var obj)
            || obj is not ConnectorBindingRegistryProvider provider)
        {
            _logger.LogVerbose(
                $"[Roslyn] Project '{project.ProjectName}' has no binding provider yet; skipping.");
            return;
        }

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return;

        cancellationToken.ThrowIfCancellationRequested();

        var file = FileDetails.FromPath(filePath).WithCSharpContent(text);
        await provider.ApplyRoslynFileUpdateAsync(file).ConfigureAwait(false);

        _logger.LogInfo(
            $"[Roslyn] Re-discovered bindings for '{Path.GetFileName(filePath)}' " +
            $"in project '{project.ProjectName}'.");
    }
}
