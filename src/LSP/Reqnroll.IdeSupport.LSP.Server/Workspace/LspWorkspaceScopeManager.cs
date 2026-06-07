using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// Thread-safe implementation of <see cref="ILspWorkspaceScopeManager"/>.
/// </summary>
public sealed class LspWorkspaceScopeManager : ILspWorkspaceScopeManager, IDisposable
{
    private readonly IIdeScope _ideScope;
    private readonly IDeveroomLogger _logger;
    private readonly IMediator _mediator;

    private readonly ConcurrentDictionary<string, LspProjectScope> _scopes
        = new(StringComparer.OrdinalIgnoreCase);

    // ── Membership index (Q17) ────────────────────────────────────────────────
    // path (normalised, OrdinalIgnoreCase) → { ProjectKey → ProjectFileRole }
    private readonly Dictionary<string, Dictionary<ProjectKey, ProjectFileRole>> _membership
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _membershipLock = new();
    // project key → baseline-received flag
    private readonly ConcurrentDictionary<ProjectKey, bool> _baselineReceived = new();

    public LspWorkspaceScopeManager(IIdeScope ideScope, IDeveroomLogger logger, IMediator mediator)
    {
        _ideScope  = ideScope;
        _logger    = logger;
        _mediator  = mediator;
    }

    // ── Folder lifecycle ──────────────────────────────────────────────────────

    public event Action<LspProjectScope>? ScopeOpened;
    public event Action<LspProjectScope>? ScopeClosed;

    public void OpenWorkspace(string rootPath)
    {
        var key = Normalise(rootPath);
        LspProjectScope? added = null;
        _scopes.GetOrAdd(key, k =>
        {
            _logger.LogInfo($"Opening workspace scope: {k}");
            added = new LspProjectScope(k, _ideScope);
            return added;
        });
        if (added is not null)
            ScopeOpened?.Invoke(added);
    }

    public void CloseWorkspace(string rootPath)
    {
        var key = Normalise(rootPath);
        if (!_scopes.TryRemove(key, out var scope))
            return;

        _logger.LogInfo($"Closing workspace scope: {key}");

        // Raise ProjectRemoved for every project still inside the scope.
        foreach (var project in scope.Projects)
        {
            ProjectRemoved?.Invoke(project);
        }

        ScopeClosed?.Invoke(scope);
        scope.Dispose();
    }

    // ── Project lifecycle ─────────────────────────────────────────────────────

    public event Action<LspReqnrollProject>? ProjectDiscovered;
    public event Action<LspReqnrollProject>? ProjectRemoved;

    public Task HandleProjectLoadedAsync(
        ReqnrollProjectLoadedParams parameters,
        CancellationToken cancellationToken)
    {
        // Ensure the workspace folder exists (create it if the IDE sends the project
        // notification before the LSP initialize workspace-folders arrive).
        var folderKey = Normalise(parameters.WorkspaceFolder);
        var scope = _scopes.GetOrAdd(folderKey, k =>
        {
            _logger.LogInfo($"Auto-creating workspace scope for project notification: {k}");
            var newScope = new LspProjectScope(k, _ideScope);
            ScopeOpened?.Invoke(newScope);
            return newScope;
        });

        var (project, isNew, discoveryInputChanged) = scope.AddOrUpdateProject(parameters);

        if (isNew)
        {
            _logger.LogInfo(
                $"Project discovered: {project.ProjectName} " +
                $"[{project.TargetFrameworkMoniker}] → {project.OutputAssemblyPath}");
            // ProjectDiscovered subscribers (BindingRegistryProviderRouter) create the
            // per-project provider and trigger the initial discovery, so no explicit
            // refresh is needed here for a brand-new project.
            ProjectDiscovered?.Invoke(project);
        }
        else
        {
            _logger.LogInfo(
                $"Project updated: {project.ProjectName} " +
                $"[{project.TargetFrameworkMoniker}] → {project.OutputAssemblyPath}");

            // An existing project whose output assembly path or target framework changed
            // (e.g. a rebuild, or a Debug→Release switch that moves the output path) must
            // re-run binding discovery.  The output-assembly file watcher does not reliably
            // cover the path-change case: GetProjectByOutputPath matches on the *old* path
            // until this update lands, so the watcher event for the new DLL can be dropped.
            if (discoveryInputChanged)
                TriggerBindingDiscovery(project);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Triggers a debounced binding re-discovery on the per-project
    /// <see cref="ConnectorBindingRegistryProvider"/> stored in the project's property bag,
    /// if one has been registered by <see cref="BindingRegistryProviderRouter"/>.
    /// </summary>
    private void TriggerBindingDiscovery(LspReqnrollProject project)
    {
        if (project.Properties.TryGetValue(
                typeof(ConnectorBindingRegistryProvider), out var obj)
            && obj is ConnectorBindingRegistryProvider provider)
        {
            _logger.LogVerbose(
                $"[{project.ProjectName}] Discovery inputs changed; triggering re-discovery.");
            provider.TriggerRefresh();
        }
        else
        {
            _logger.LogVerbose(
                $"[{project.ProjectName}] Discovery inputs changed but no binding provider " +
                $"registered yet; skipping refresh.");
        }
    }

    public Task HandleProjectUnloadedAsync(
        ReqnrollProjectUnloadedParams parameters,
        CancellationToken cancellationToken)
    {
        foreach (var scope in _scopes.Values)
        {
            var removed = scope.RemoveProject(parameters.ProjectFile);
            if (removed is null)
                continue;

            _logger.LogInfo($"Project removed: {removed.ProjectName}");
            ProjectRemoved?.Invoke(removed);
            removed.Dispose();
            return Task.CompletedTask;
        }

        _logger.LogVerbose(
            $"HandleProjectUnloadedAsync: no project found for {parameters.ProjectFile}");
        return Task.CompletedTask;
    }

    // ── Lookup ────────────────────────────────────────────────────────────────

    public LspProjectScope? GetScopeForUri(DocumentUri uri)
    {
        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return null;

        return _scopes.Values
            .Where(s => filePath.StartsWith(s.RootFolder, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.RootFolder.Length)
            .FirstOrDefault();
    }

    public LspReqnrollProject? GetProjectForUri(DocumentUri uri)
    {
        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return null;

        return _scopes.Values
            .SelectMany(s => s.Projects)
            .Where(p => filePath.StartsWith(p.ProjectFolder, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.ProjectFolder.Length)
            .FirstOrDefault();
    }

    public LspReqnrollProject? GetProjectByOutputPath(string assemblyPath)
    {
        if (string.IsNullOrEmpty(assemblyPath))
            return null;

        return _scopes.Values
            .SelectMany(s => s.Projects)
            .FirstOrDefault(p => string.Equals(
                p.OutputAssemblyPath, assemblyPath,
                StringComparison.OrdinalIgnoreCase));
    }

    public IDeveroomConfigurationProvider GetConfigurationProviderForUri(DocumentUri uri)
    {
        var project = GetProjectForUri(uri);
        if (project is not null)
            return project.GetDeveroomConfigurationProvider();

        // Fallback: default configuration when no project covers the URI.
        return new ProjectSystemDeveroomConfigurationProvider(_ideScope);
    }

    // ── Membership index (Q17) ────────────────────────────────────────────────

    public async Task HandleProjectFilesAsync(
        ReqnrollProjectFilesParams parameters,
        CancellationToken cancellationToken)
    {
        var key = MakeKey(parameters.ProjectFile, parameters.TargetFrameworkMoniker);

        if (parameters.Kind == ProjectFilesKind.Delta)
        {
            if (!_baselineReceived.ContainsKey(key))
            {
                _logger.LogVerbose(
                    $"[Membership] Dropping delta for '{parameters.ProjectFile}': " +
                    "no baseline received yet.");
                return;
            }
            ApplyDelta(key, parameters.Files);
            return;
        }

        // Baseline: replace this project's contribution wholesale.
        lock (_membershipLock)
        {
            // Remove the project from every path it previously claimed.
            foreach (var path in _membership.Keys.ToList())
            {
                var owners = _membership[path];
                if (owners.Remove(key) && owners.Count == 0)
                    _membership.Remove(path);
            }

            // Add all incoming paths.
            foreach (var entry in parameters.Files)
            {
                var normPath = NormaliseFilePath(entry.Path);
                if (!_membership.TryGetValue(normPath, out var owners))
                {
                    owners = new Dictionary<ProjectKey, ProjectFileRole>();
                    _membership[normPath] = owners;
                }
                owners[key] = entry.Role;
            }
        }

        _baselineReceived[key] = true;

        _logger.LogInfo(
            $"[Membership] Baseline received for '{parameters.ProjectFile}' " +
            $"[{parameters.TargetFrameworkMoniker}]: {parameters.Files.Length} file(s).");

        // Trigger a full re-scan for the project so diagnostics reflect the new index.
        var project = FindProjectByKey(key);
        if (project is not null)
        {
            _ = _mediator.Publish(
                new BindingRegistryChangedNotification(project, true),
                cancellationToken);
        }
        else
        {
            _logger.LogVerbose(
                $"[Membership] No live project found for '{parameters.ProjectFile}'; " +
                "re-scan deferred until the project loads.");
        }
    }

    public IReadOnlyCollection<LspReqnrollProject> GetProjectsForUri(DocumentUri uri)
    {
        var filePath = NormaliseFilePath(uri.GetFileSystemPath() ?? string.Empty);
        if (string.IsNullOrEmpty(filePath))
            return [];

        Dictionary<ProjectKey, ProjectFileRole>? owners;
        lock (_membershipLock)
        {
            _membership.TryGetValue(filePath, out owners);
        }

        if (owners is null or { Count: 0 })
            return [];

        var result = new List<LspReqnrollProject>(owners.Count);
        foreach (var key in owners.Keys)
        {
            var project = FindProjectByKey(key);
            if (project is not null)
                result.Add(project);
        }
        return result;
    }

    public IReadOnlyCollection<LspReqnrollProject> ResolveOwners(DocumentUri uri)
    {
        var indexOwners = GetProjectsForUri(uri);
        if (indexOwners.Count > 0)
            return indexOwners;

        // Fall back to folder-prefix for files whose covering project hasn't sent a baseline.
        if (GetMembershipState(uri) == MembershipState.Pending)
        {
            var fallback = GetProjectForUri(uri);
            return fallback is not null ? [fallback] : [];
        }

        return [];  // Unowned
    }

    public LspReqnrollProject? ResolvePrimaryOwner(DocumentUri uri)
    {
        var owners = ResolveOwners(uri);
        if (owners.Count == 0)
            return null;
        if (owners.Count == 1)
            return owners.First();

        var filePath = uri.GetFileSystemPath() ?? string.Empty;

        // Prefer the owner whose ProjectFolder is a prefix of the file path (home project).
        // If several qualify, pick the longest prefix (most specific containing project).
        var homeOwners = owners
            .Where(p => !string.IsNullOrEmpty(p.ProjectFolder) &&
                        filePath.StartsWith(p.ProjectFolder, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.ProjectFolder.Length)
            .ToList();

        if (homeOwners.Count > 0)
            return homeOwners[0];

        // File is outside every owner's folder (genuinely external/linked). Use ordinal tiebreak
        // on ProjectFullName so the result is stable regardless of baseline-arrival order.
        return owners
            .OrderBy(p => p.ProjectFullName, StringComparer.Ordinal)
            .First();
    }

    public MembershipState GetMembershipState(DocumentUri uri)
    {
        var filePath = NormaliseFilePath(uri.GetFileSystemPath() ?? string.Empty);
        if (string.IsNullOrEmpty(filePath))
            return MembershipState.Unowned;

        lock (_membershipLock)
        {
            if (_membership.ContainsKey(filePath))
                return MembershipState.Owned;
        }

        // Any project that would cover this path via folder-prefix?
        var covering = _scopes.Values
            .SelectMany(s => s.Projects)
            .Where(p => filePath.StartsWith(p.ProjectFolder, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (covering.Count == 0)
            return MembershipState.Unowned;

        // Pending if any covering project has not yet sent a baseline.
        foreach (var project in covering)
        {
            if (!_baselineReceived.ContainsKey(MakeKey(project)))
                return MembershipState.Pending;
        }

        return MembershipState.Unowned;
    }

    public IReadOnlyCollection<string> GetIndexedFeatureFiles(LspReqnrollProject project)
    {
        var key = MakeKey(project);
        lock (_membershipLock)
        {
            return _membership
                .Where(kvp =>
                    kvp.Value.TryGetValue(key, out var role) &&
                    role == ProjectFileRole.Feature)
                .Select(kvp => kvp.Key)
                .ToList();
        }
    }

    public bool HasBaselineForProject(LspReqnrollProject project)
        => _baselineReceived.ContainsKey(MakeKey(project));

    private void ApplyDelta(ProjectKey key, ProjectFileEntry[] entries)
    {
        lock (_membershipLock)
        {
            foreach (var entry in entries)
            {
                var normPath = NormaliseFilePath(entry.Path);
                if (entry.Added)
                {
                    if (!_membership.TryGetValue(normPath, out var owners))
                    {
                        owners = new Dictionary<ProjectKey, ProjectFileRole>();
                        _membership[normPath] = owners;
                    }
                    owners[key] = entry.Role;
                }
                else
                {
                    if (_membership.TryGetValue(normPath, out var owners))
                    {
                        owners.Remove(key);
                        if (owners.Count == 0)
                            _membership.Remove(normPath);
                    }
                }
            }
        }
    }

    private LspReqnrollProject? FindProjectByKey(ProjectKey key)
    {
        // Phase 1: match by ProjectFile only (TFM keying is a planned follow-up).
        return _scopes.Values
            .SelectMany(s => s.Projects)
            .FirstOrDefault(p => string.Equals(
                NormaliseFilePath(p.ProjectFullName),
                key.ProjectFile,
                StringComparison.OrdinalIgnoreCase));
    }

    private static ProjectKey MakeKey(string projectFile, string tfm)
        => new(NormaliseFilePath(projectFile), tfm);

    private static ProjectKey MakeKey(LspReqnrollProject project)
        => new(NormaliseFilePath(project.ProjectFullName), project.TargetFrameworkMoniker);

    private static string NormaliseFilePath(string path)
        => string.IsNullOrEmpty(path) ? path : Path.GetFullPath(path);

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (var key in _scopes.Keys.ToArray())
            CloseWorkspace(key);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Normalise(string path)
        => Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
}
