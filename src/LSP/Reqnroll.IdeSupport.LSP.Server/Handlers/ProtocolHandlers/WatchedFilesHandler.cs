using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

/// <summary>
/// Handles <c>workspace/didChangeWatchedFiles</c> notifications.
/// <list type="bullet">
///   <item><term>reqnroll.json changes</term>
///     <description>Reload the owning project's configuration and trigger binding re-discovery.</description>
///   </item>
///   <item><term>Output assembly changes (<c>bin/**/*.dll</c>)</term>
///     <description>Trigger binding re-discovery for the project whose output path matches.</description>
///   </item>
/// </list>
/// </summary>
public class WatchedFilesHandler : IDidChangeWatchedFilesHandler
{
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IMediator                 _mediator;
    private readonly IDeveroomLogger            _logger;

    public WatchedFilesHandler(
        ILspWorkspaceScopeManager scopeManager,
        IMediator mediator,
        IDeveroomLogger logger)
    {
        _scopeManager = scopeManager;
        _mediator     = mediator;
        _logger       = logger;
    }

    public DidChangeWatchedFilesRegistrationOptions GetRegistrationOptions(
        DidChangeWatchedFilesCapability capability,
        ClientCapabilities clientCapabilities)
        => new()
        {
            Watchers = new[]
            {
#pragma warning disable CS8601 // GlobPattern implicit conversion from string returns GlobPattern? but value is provably non-null
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = "**/reqnroll.json",
                    Kind        = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                },
                // Broad watcher for rebuilt output assemblies.  Narrowed to the specific
                // OutputAssemblyPath per project once a reqnroll/projectLoaded notification
                // has been received and the path is known.
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = "**/bin/**/*.dll",
                    Kind        = WatchKind.Create | WatchKind.Change
                }
#pragma warning restore CS8601
            }
        };

    public async Task<MediatR.Unit> Handle(
        DidChangeWatchedFilesParams request,
        CancellationToken cancellationToken)
    {
        foreach (var fileEvent in request.Changes)
        {
            var uri        = fileEvent.Uri;
            var changeType = fileEvent.Type;
            var filePath   = uri.GetFileSystemPath() ?? string.Empty;

            if (IsReqnrollConfig(filePath))
            {
                await HandleConfigChangeAsync(uri, filePath, changeType, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (IsOutputAssembly(filePath))
            {
                HandleOutputAssemblyChange(filePath, changeType);
            }
        }

        return MediatR.Unit.Value;
    }

    // ── Config change ─────────────────────────────────────────────────────────

    private async Task HandleConfigChangeAsync(
        OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri uri,
        string filePath,
        FileChangeType changeType,
        CancellationToken ct)
    {
        var project = _scopeManager.GetProjectForUri(uri);
        if (project is null)
        {
            _logger.LogVerbose(
                $"reqnroll.json event ({changeType}) for {filePath} — no matching project, skipping.");
            return;
        }

        _logger.LogInfo(
            $"reqnroll.json {changeType}: reloading config for project '{project.ProjectName}'");

        var provider = project.GetDeveroomConfigurationProvider()
            as ProjectScopeDeveroomConfigurationProvider;
        provider?.Reload();

        await _mediator.Publish(
            new ReqnrollConfigChangedNotification(project.ProjectFolder), ct)
            .ConfigureAwait(false);

        // Config change can affect discovery inputs — trigger a re-run.
        TriggerBindingDiscovery(project, "config change");
    }

    // ── Output assembly change ────────────────────────────────────────────────

    private void HandleOutputAssemblyChange(string filePath, FileChangeType changeType)
    {
        var project = _scopeManager.GetProjectByOutputPath(filePath);
        if (project is null)
        {
            // The changed assembly may not belong to any registered Reqnroll project;
            // this is expected for dependency assemblies.
            _logger.LogVerbose(
                $"Output assembly {changeType}: {filePath} — no matching project output path.");
            return;
        }

        _logger.LogVerbose(
            $"Output assembly {changeType}: triggering discovery for '{project.ProjectName}'");
        TriggerBindingDiscovery(project, "output assembly change");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsReqnrollConfig(string filePath)
        => Path.GetFileName(filePath).Equals(
            "reqnroll.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsOutputAssembly(string filePath)
        => filePath.IndexOf(
               Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase) >= 0
           && filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private void TriggerBindingDiscovery(LspReqnrollProject project, string reason)
    {
        if (project.Properties.TryGetValue(
                typeof(ConnectorBindingRegistryProvider), out var obj)
            && obj is ConnectorBindingRegistryProvider provider)
        {
            provider.TriggerRefresh();
        }
        else
        {
            _logger.LogVerbose(
                $"[{project.ProjectName}] No ConnectorBindingRegistryProvider in Properties " +
                $"(reason: {reason}); skipping discovery trigger.");
        }
    }
}
