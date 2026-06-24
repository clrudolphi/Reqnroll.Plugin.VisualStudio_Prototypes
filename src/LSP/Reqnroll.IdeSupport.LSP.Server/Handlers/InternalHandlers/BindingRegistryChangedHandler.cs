using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
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
    private readonly IDocumentBufferService         _documentBufferService;
    private readonly IGherkinDocumentTaggerService   _taggerService;
    private readonly ILspWorkspaceScopeManager       _scopeManager;
    private readonly ILanguageServerFacade            _languageServer;
    private readonly ClientIdeContext                 _clientIde;
    private readonly IMediator                        _mediator;
    private readonly ICSharpBindingDiscoveryService   _csharpDiscoveryService;
    private readonly IDeveroomLogger                  _logger;

    public BindingRegistryChangedHandler(
        IDocumentBufferService documentBufferService,
        IGherkinDocumentTaggerService taggerService,
        ILspWorkspaceScopeManager scopeManager,
        ILanguageServerFacade languageServer,
        ClientIdeContext clientIde,
        IMediator mediator,
        ICSharpBindingDiscoveryService csharpDiscoveryService,
        IDeveroomLogger logger)
    {
        _documentBufferService  = documentBufferService;
        _taggerService          = taggerService;
        _scopeManager           = scopeManager;
        _languageServer         = languageServer;
        _clientIde              = clientIde;
        _mediator               = mediator;
        _csharpDiscoveryService = csharpDiscoveryService;
        _logger                 = logger;
    }

    public async Task Handle(
        BindingRegistryChangedNotification notification,
        CancellationToken cancellationToken)
    {
        if (notification.IsFullReplacement)
        {
            // After a Connector full replacement, re-discover bindings from the project's .cs
            // files using Roslyn source-level discovery.  The Connector provides bindings from
            // the compiled DLL, which may be stale if the user renamed/edited bindings without
            // rebuilding.  Source-level discovery replaces the stale compiled entries with fresh
            // source-level data, preventing "Step definition not found" (and the inverse, a step
            // still shown as bound to a renamed binding) on files edited but not rebuilt.
            await RediscoverCsFilesAsync(notification.Project, cancellationToken)
                .ConfigureAwait(false);

            await ScanAllFeatureFilesAsync(notification.Project, cancellationToken).ConfigureAwait(false);
        }

        await ReparseOpenFilesAsync(notification.Project, cancellationToken).ConfigureAwait(false);

        // Q23 Piece 2b: after the binding registry is populated (Connector run complete),
        // ask the client to refresh its code lens. Without this, a .cs file that was the
        // foreground editor at startup keeps the (count-less) code lenses it rendered before the
        // server was ready, until the user navigates away and back to re-realize the view.
        if (notification.IsFullReplacement)
            await RequestCodeLensRefreshAsync(notification.Project).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the client to re-pull C# step code lenses after a full registry replacement.
    /// Visual Studio cannot route the standard <c>workspace/codeLens/refresh</c> request to our
    /// pipe-based code-lens provider (the VS client's <c>StepCodeLensService</c> uses the
    /// <c>LspInterceptingPipe</c> directly, not VS's built-in LSP code-lens infrastructure), so for
    /// VS we push a custom <c>reqnroll/refreshCodeLens</c> notification the client intercepts to
    /// invalidate its rendered lenses. VS Code / Rider use the standard request.
    /// </summary>
    private async Task RequestCodeLensRefreshAsync(LspReqnrollProject project)
    {
        if (_clientIde.IsVisualStudio)
        {
            _logger.LogInfo(
                $"BindingRegistryChanged: sending reqnroll/refreshCodeLens for project '{project.ProjectName}'.");
            try
            {
                _languageServer.SendNotification(
                    LspMethodNames.ReqnrollRefreshCodeLens,
                    new RefreshCodeLensParams { ProjectName = project.ProjectName });
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"reqnroll/refreshCodeLens failed: {ex.Message}");
            }
            return;
        }

        _logger.LogInfo(
            $"BindingRegistryChanged: sending workspace/codeLens/refresh for project '{project.ProjectName}'.");
        try
        {
            await _languageServer.Client
                .SendRequest(LspMethodNames.WorkspaceCodeLensRefresh)
                .ReturningVoid(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"workspace/codeLens/refresh failed: {ex.Message}");
        }
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

    /// <summary>
    /// After a Connector full replacement (which loads bindings from the compiled assembly),
    /// re-runs Roslyn source-level discovery on the project's <c>.cs</c> step-definition files
    /// so that source edited <em>since the last build</em> overrides the stale compiled bindings.
    /// <para>Covers two cases:</para>
    /// <list type="bullet">
    ///   <item>open, possibly-unsaved <c>.cs</c> buffers — reconciled unconditionally, since an
    ///   unsaved edit is never reflected in the DLL; and</item>
    ///   <item>closed <c>.cs</c> files on disk whose last-write time is newer than the output
    ///   assembly — i.e. edited then saved without a rebuild. This is the case that survives a VS
    ///   restart: the file is on disk but not open, so without this it would never override the
    ///   stale compiled binding.</item>
    /// </list>
    /// Files unchanged since the build are faithfully represented by the DLL and are skipped to
    /// bound the cost. Reconciliation is delegated to <see cref="ICSharpBindingDiscoveryService"/>,
    /// which patches the project's registry directly without consulting the membership index
    /// (the baseline may not have arrived yet at startup).
    /// </summary>
    private async Task RediscoverCsFilesAsync(LspReqnrollProject project, CancellationToken ct)
    {
        var filesToReconcile = CollectCsFilesToReconcile(project);
        if (filesToReconcile.Count == 0)
            return;

        _logger.LogInfo(
            $"[Connector startup] Roslyn-reconciling {filesToReconcile.Count} .cs file(s) for project " +
            $"'{project.ProjectName}' to override potentially stale compiled bindings.");

        foreach (var (filePath, text) in filesToReconcile)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _csharpDiscoveryService
                    .UpdateFromSourceForProjectAsync(project, filePath, text, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    $"[Connector startup] Roslyn rediscovery failed for '{filePath}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Selects the <c>.cs</c> files to reconcile after a full replacement and pairs each with the
    /// source text to parse: every open project-owned <c>.cs</c> buffer (unsaved edits always win,
    /// using the buffer text), plus closed step-definition files newer than the compiled assembly
    /// (using on-disk text).
    /// </summary>
    private List<(string FilePath, string Text)> CollectCsFilesToReconcile(LspReqnrollProject project)
    {
        var projectFolder = project.ProjectFolder;
        if (string.IsNullOrEmpty(projectFolder))
            return [];

        // 1. Open, project-owned .cs buffers — unsaved edits override the DLL regardless of mtime.
        //    Folder-prefix (not the index) is used deliberately: at startup the membership baseline
        //    may not have arrived, and these files are known to be in the editor anyway.
        var openByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var buffer in _documentBufferService.All)
        {
            var path = buffer.Uri.GetFileSystemPath();
            if (!string.IsNullOrEmpty(path)
                && path!.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(buffer.Text)
                && path.StartsWith(projectFolder, StringComparison.OrdinalIgnoreCase))
            {
                openByPath[path] = buffer.Text;
            }
        }

        var result = openByPath.Select(kvp => (kvp.Key, kvp.Value)).ToList();

        // 2. Closed .cs step-def files edited since the last build (newer than the assembly).
        //    No assembly (never built) => nothing compiled can be stale => only the open buffers
        //    above are relevant.
        var assemblyWriteTimeUtc = GetAssemblyWriteTimeUtc(project);
        if (assemblyWriteTimeUtc is null)
            return result;

        foreach (var path in EnumerateProjectStepDefinitionFiles(project))
        {
            if (openByPath.ContainsKey(path))
                continue; // already covered by its open buffer above

            DateTime mtimeUtc;
            try { mtimeUtc = File.GetLastWriteTimeUtc(path); }
            catch { continue; }

            if (mtimeUtc <= assemblyWriteTimeUtc.Value)
                continue; // unchanged since the build → the compiled binding is authoritative

            try
            {
                result.Add((path, File.ReadAllText(path)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"[Connector startup] Could not read '{path}' for Roslyn rediscovery: {ex.Message}");
            }
        }

        return result;
    }

    private static DateTime? GetAssemblyWriteTimeUtc(LspReqnrollProject project)
    {
        var assemblyPath = project.OutputAssemblyPath;
        if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
            return null;
        try { return File.GetLastWriteTimeUtc(assemblyPath); }
        catch { return null; }
    }

    /// <summary>
    /// Enumerates the project's <c>.cs</c> step-definition files: the membership index when a
    /// baseline has been received (authoritative — includes linked files, excludes obj/bin),
    /// otherwise a folder glob that skips build output.
    /// </summary>
    private IReadOnlyCollection<string> EnumerateProjectStepDefinitionFiles(LspReqnrollProject project)
    {
        if (_scopeManager.HasBaselineForProject(project))
            return _scopeManager.GetBindingFilePathsForProject(project);

        var folder = project.ProjectFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return [];

        return Directory
            .EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsInBuildOutput(p, folder))
            .ToList();
    }

    private static bool IsInBuildOutput(string path, string projectFolder)
    {
        var relative = path.Substring(projectFolder.Length).Replace('\\', '/');
        return relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }
}
