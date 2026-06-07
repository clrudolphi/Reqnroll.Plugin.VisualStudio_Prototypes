using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery;

/// <summary>
/// Singleton <see cref="IProjectBindingRegistryLookup"/> registered in the DI container.
/// Owns one <see cref="ConnectorBindingRegistryProvider"/> per <see cref="LspReqnrollProject"/>
/// and routes registry lookups to the provider that owns the requested document's project.
/// </summary>
/// <remarks>
/// <para>
/// Subscribes to <see cref="ILspWorkspaceScopeManager.ProjectDiscovered"/> /
/// <see cref="ILspWorkspaceScopeManager.ProjectRemoved"/> so that per-project providers
/// are created and torn down automatically as the IDE glue sends project notifications.
/// </para>
/// <para>
/// The per-project provider is also stored in
/// <see cref="LspReqnrollProject.Properties"/>[<c>typeof(ConnectorBindingRegistryProvider)</c>]
/// so that <see cref="Handlers.ProtocolHandlers.WatchedFilesHandler"/> can reach it
/// for output-assembly change events without going through this singleton.
/// </para>
/// <para>
/// Registries are intentionally <b>not</b> merged: step definitions belong to a single
/// project and a feature file should only be matched against the bindings of its own project.
/// <see cref="GetRegistryForUri"/> routes to the correct per-project registry via
/// <see cref="ILspWorkspaceScopeManager.ResolvePrimaryOwner"/> (Q18 2A: deterministic
/// home-project rule; no nondeterminism from baseline-arrival order).
/// </para>
/// <para>
/// When any project's registry is replaced the router publishes a
/// <see cref="BindingRegistryChangedNotification"/> via MediatR so that open feature files
/// belonging to that project are re-parsed and semantic tokens refreshed.
/// </para>
/// </remarks>
public sealed class BindingRegistryProviderRouter : IProjectBindingRegistryLookup, IDisposable
{
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IMediator                 _mediator;
    private readonly IBindingMatchService      _matchService;
    private readonly IDeveroomLogger            _logger;

    // Store (provider, handler) together so Dispose can unsubscribe by the exact delegate
    // that was passed to += in OnProjectDiscovered.
    private readonly ConcurrentDictionary<
        LspReqnrollProject,
        (ConnectorBindingRegistryProvider Provider, EventHandler<bool> Handler)>
        _entries = new();

    public BindingRegistryProviderRouter(
        ILspWorkspaceScopeManager scopeManager,
        IMediator mediator,
        IBindingMatchService matchService,
        IDeveroomLogger logger)
    {
        _scopeManager = scopeManager;
        _mediator     = mediator;
        _matchService = matchService;
        _logger       = logger;

        scopeManager.ProjectDiscovered += OnProjectDiscovered;
        scopeManager.ProjectRemoved    += OnProjectRemoved;
    }

    // ── IProjectBindingRegistryLookup ─────────────────────────────────────────

    /// <inheritdoc/>
    public ProjectBindingRegistry GetRegistryForUri(DocumentUri uri)
    {
        // Q18 2A: use the deterministic primary owner (home-project rule) instead of
        // nondeterministic FirstOrDefault() on the full owner set.
        var project = _scopeManager.ResolvePrimaryOwner(uri);
        if (project is null)
            return ProjectBindingRegistry.Invalid;

        return project.Properties.TryGetValue(typeof(ConnectorBindingRegistryProvider), out var obj)
               && obj is ConnectorBindingRegistryProvider provider
            ? provider.Current
            : ProjectBindingRegistry.Invalid;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _scopeManager.ProjectDiscovered -= OnProjectDiscovered;
        _scopeManager.ProjectRemoved    -= OnProjectRemoved;

        foreach (var (_, (provider, handler)) in _entries.ToArray())
        {
            provider.BindingRegistryChanged -= handler;
            provider.Dispose();
        }
        _entries.Clear();
    }

    // ── Project lifecycle ─────────────────────────────────────────────────────

    private void OnProjectDiscovered(LspReqnrollProject project)
    {
        var provider = new ConnectorBindingRegistryProvider(project, _logger);

        // Capture project in a named local so the closure below can reference it.
        // Store the delegate so Dispose can unsubscribe by identity.
        EventHandler<bool> handler = (_, isFullReplacement) => OnProviderChanged(project, isFullReplacement);
        provider.BindingRegistryChanged += handler;

        _entries[project] = (provider, handler);

        // Store in the project's property bag so WatchedFilesHandler can reach the
        // provider by output-assembly path without going through this router.
        project.Properties[typeof(ConnectorBindingRegistryProvider)] = provider;

        _logger.LogVerbose(
            $"[Router] Registered binding provider for '{project.ProjectName}'.");

        // Kick off an initial discovery attempt; no-ops if OutputAssemblyPath is empty.
        provider.TriggerRefresh();
    }

    private void OnProjectRemoved(LspReqnrollProject project)
    {
        if (!_entries.TryRemove(project, out var entry))
            return;

        entry.Provider.BindingRegistryChanged -= entry.Handler;
        entry.Provider.Dispose();

        // Drop every (*, project) match-set entry so stale data does not linger.
        _matchService.InvalidateAllForProject(
            new ProjectOwner(project.ProjectFullName, project.TargetFrameworkMoniker));

        _logger.LogVerbose(
            $"[Router] Removed binding provider for '{project.ProjectName}'.");
    }

    // ── Change notification ───────────────────────────────────────────────────

    private void OnProviderChanged(LspReqnrollProject project, bool isFullReplacement)
    {
        _logger.LogVerbose(
            $"[Router] Binding registry updated for '{project.ProjectName}' " +
            $"(fullReplacement={isFullReplacement}); publishing notification.");

        _ = _mediator.Publish(new BindingRegistryChangedNotification(project, isFullReplacement));
    }
}
