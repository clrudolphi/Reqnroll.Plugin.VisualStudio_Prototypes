using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
using Reqnroll.IdeSupport.LSP.Server.Services;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.InternalHandlers;

/// <summary>
/// Handles <see cref="BindingRegistryChangedNotification"/> by re-parsing feature files
/// that belong to the affected project, then publishing a
/// <see cref="MatchCacheChangedNotification"/> for each open file so that semantic tokens
/// are refreshed against the new binding registry.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="BindingRegistryChangedNotification.IsFullReplacement"/> is
/// <see langword="true"/> (startup, post-build connector run, or membership-baseline arrival),
/// all feature files owned by the project are scanned — including files not currently open
/// in the editor — so that the binding match cache is workspace-complete for F14 Find Usages.
/// The file list is obtained from the membership index (I1) when a baseline has been received;
/// otherwise it falls back to a folder glob for backwards compatibility with clients that do
/// not send <c>reqnroll/projectFiles</c>.
/// </para>
/// <para>
/// When <see cref="BindingRegistryChangedNotification.IsFullReplacement"/> is
/// <see langword="false"/> (incremental Roslyn per-file patch on a <c>.cs</c> edit), only the
/// currently open feature files owned by the project are re-parsed.
/// </para>
/// </remarks>
public class BindingRegistryChangedHandler : INotificationHandler<BindingRegistryChangedNotification>
{
    private readonly IDocumentBufferService        _documentBufferService;
    private readonly IGherkinDocumentTaggerService  _taggerService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IMediator                     _mediator;
    private readonly IDeveroomLogger                _logger;

    public BindingRegistryChangedHandler(
        IDocumentBufferService documentBufferService,
        IGherkinDocumentTaggerService taggerService,
        ILspWorkspaceScopeManager scopeManager,
        IMediator mediator,
        IDeveroomLogger logger)
    {
        _documentBufferService = documentBufferService;
        _taggerService         = taggerService;
        _scopeManager          = scopeManager;
        _mediator              = mediator;
        _logger                = logger;
    }

    public async Task Handle(
        BindingRegistryChangedNotification notification,
        CancellationToken cancellationToken)
    {
        if (notification.IsFullReplacement)
            await ScanAllFeatureFilesAsync(notification.Project, cancellationToken).ConfigureAwait(false);

        await ReparseOpenFilesAsync(notification.Project, cancellationToken).ConfigureAwait(false);
    }

    private async Task ScanAllFeatureFilesAsync(
        LspReqnrollProject project,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> allFeatureFiles;

        if (_scopeManager.HasBaselineForProject(project))
        {
            // I1: use the authoritative index — this correctly includes linked features
            // outside the project folder and excludes removed/conditional ones inside it.
            allFeatureFiles = _scopeManager.GetIndexedFeatureFiles(project);
        }
        else
        {
            // Legacy fallback: project has never sent reqnroll/projectFiles (e.g. VS Code
            // interim, or startup race before the first baseline arrives).
            var projectFolder = project.ProjectFolder;
            if (string.IsNullOrEmpty(projectFolder) || !Directory.Exists(projectFolder))
                return;

            allFeatureFiles = Directory
                .EnumerateFiles(projectFolder, "*.feature", SearchOption.AllDirectories)
                .ToList();
        }

        // Skip files already open — ReparseOpenFilesAsync will handle those via the buffer.
        var openUris = _documentBufferService.All
            .Select(b => b.Uri.GetFileSystemPath())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var closedFiles = allFeatureFiles
            .Where(f => !openUris.Contains(f))
            .ToList();

        _logger.LogInfo(
            $"Full registry replacement — scanning {closedFiles.Count} closed feature file(s) " +
            $"for project '{project.ProjectName}'.");

        foreach (var filePath in closedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                var uri  = DocumentUri.FromFileSystemPath(filePath);
                await _taggerService.ScanClosedFileAsync(uri, text, project).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning($"ScanAllFeatureFiles: could not scan '{filePath}': {ex.Message}");
            }
        }
    }

    private async Task ReparseOpenFilesAsync(
        LspReqnrollProject project,
        CancellationToken cancellationToken)
    {
        // Select open feature buffers that belong to the changed project.
        // Use the membership index when a baseline has been received (I1); fall back to
        // folder-prefix for projects that haven't sent reqnroll/projectFiles.
        var affectedBuffers = _documentBufferService.All
            .Where(b => IsOwnedByProject(b.Uri, project))
            .ToList();

        if (affectedBuffers.Count == 0)
        {
            _logger.LogVerbose(
                $"BindingRegistryChanged — no open feature files to reparse for '{project.ProjectName}'.");
            return;
        }

        _logger.LogInfo(
            $"BindingRegistryChanged — reparsing {affectedBuffers.Count} open feature file(s) " +
            $"for project '{project.ProjectName}'.");

        foreach (var buffer in affectedBuffers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ParseAndNotifyAsync(buffer.Uri, buffer.Version, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private bool IsOwnedByProject(DocumentUri uri, LspReqnrollProject project)
    {
        if (_scopeManager.HasBaselineForProject(project))
        {
            // Index-driven ownership check (I1).
            var owners = _scopeManager.GetProjectsForUri(uri);
            return owners.Contains(project);
        }

        // Fallback: folder-prefix for projects without a baseline.
        return IsUnderProjectFolder(uri, project.ProjectFolder);
    }

    private async Task ParseAndNotifyAsync(
        DocumentUri uri,
        int? version,
        CancellationToken cancellationToken)
    {
        await _taggerService.ParseAsync(uri, version).ConfigureAwait(false);
        await _mediator.Publish(
            new MatchCacheChangedNotification(uri, version ?? 0),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsUnderProjectFolder(DocumentUri uri, string projectFolder)
    {
        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(projectFolder))
            return false;

        return filePath.StartsWith(projectFolder, StringComparison.OrdinalIgnoreCase);
    }
}
