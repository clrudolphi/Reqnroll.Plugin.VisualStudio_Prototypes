using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery;

/// <summary>
/// Per-project <see cref="IBindingRegistryProvider"/> backed by
/// <see cref="ConnectorDiscoveryService"/>.
/// <para>
/// One instance lives in <see cref="LspReqnrollProject.Properties"/> (keyed by
/// <c>typeof(ConnectorBindingRegistryProvider)</c>), created by
/// <see cref="BindingRegistryProviderRouter"/> when a project is discovered.
/// </para>
/// <para>
/// Discovery runs are debounced (500 ms) and cancellable: calling
/// <see cref="TriggerRefresh"/> while a run is in flight cancels it and starts a fresh one.
/// When a run succeeds the last-good registry is replaced atomically and
/// <see cref="BindingRegistryChanged"/> is raised.  When a run fails the last-good registry
/// is kept and an error is logged; the registry is never replaced with
/// <see cref="ProjectBindingRegistry.Invalid"/> after it has been successfully populated once.
/// </para>
/// </summary>
public sealed class ConnectorBindingRegistryProvider : IBindingRegistryProvider, IDisposable
{
    private const int DebounceMilliseconds = 500;

    private readonly LspReqnrollProject _project;
    private readonly IConnectorDiscoveryService _discoveryService;
    private readonly IDeveroomLogger _logger;

    // Last-good state.  Volatile so readers always see the latest write.
    private volatile ProjectBindingRegistry _current = ProjectBindingRegistry.Invalid;
    private string _lastHash = string.Empty;

    // In-flight run guard.
    private readonly object _cts_lock = new();
    private CancellationTokenSource? _cts;

    private event EventHandler? _bindingRegistryChanged;

    /// <summary>
    /// Creates a provider backed by the default connector-based discovery service
    /// (generic/custom connector selected per project configuration).
    /// </summary>
    public ConnectorBindingRegistryProvider(LspReqnrollProject project, IDeveroomLogger logger)
        : this(project, new ConnectorDiscoveryService(logger, new OutProcReqnrollConnectorFactory(logger)), logger)
    {
    }

    /// <summary>
    /// Creates a provider backed by a caller-supplied discovery service.  Used by tests to
    /// substitute discovery so the debounce/cancellation/swap behaviour can be verified in
    /// isolation from the out-of-process connector.
    /// </summary>
    public ConnectorBindingRegistryProvider(
        LspReqnrollProject project,
        IConnectorDiscoveryService discoveryService,
        IDeveroomLogger logger)
    {
        _project          = project;
        _logger           = logger;
        _discoveryService = discoveryService;
    }

    // ── IBindingRegistryProvider ──────────────────────────────────────────────

    /// <inheritdoc/>
    public ProjectBindingRegistry Current => _current;

    /// <inheritdoc/>
    public event EventHandler? BindingRegistryChanged
    {
        add    => _bindingRegistryChanged += value;
        remove => _bindingRegistryChanged -= value;
    }

    // ── Public control ────────────────────────────────────────────────────────

    /// <summary>
    /// Schedules a debounced discovery run.  Any in-flight run is cancelled immediately;
    /// the new run starts after <c>500 ms</c> to absorb rapid successive triggers (e.g.
    /// several output files being written by a build).
    /// </summary>
    public void TriggerRefresh()
    {
        CancellationTokenSource? oldCts;
        CancellationTokenSource  newCts;

        lock (_cts_lock)
        {
            oldCts = _cts;
            newCts = new CancellationTokenSource();
            _cts   = newCts;
        }

        oldCts?.Cancel();
        oldCts?.Dispose();

        _ = Task.Run(() => RunDiscoveryAsync(newCts.Token), newCts.Token);
    }

    /// <summary>
    /// Applies an immediate, source-level (Roslyn) binding update for a single C# file on top of
    /// the current registry, replacing only that file's step definitions and hooks (design doc F2).
    /// </summary>
    /// <remarks>
    /// This is the in-process counterpart to the out-of-process reflection connector: it gives
    /// instant feedback as the user edits a step-definition file, without waiting for a build.
    /// The patch is layered on top of <see cref="Current"/> and intentionally does <b>not</b>
    /// advance the connector's last-good hash, so the next successful connector run (after a real
    /// build, whose assembly hash differs) replaces the whole registry with the authoritative
    /// post-build result. If no build has happened, the connector run is a hash-match no-op and
    /// the Roslyn patch survives.
    /// </remarks>
    public async Task ApplyRoslynFileUpdateAsync(CSharpStepDefinitionFile file)
    {
        var updated = await _current.ReplaceBindings(file).ConfigureAwait(false);
        _current = updated;
        _bindingRegistryChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_cts_lock)
        {
            cts  = _cts;
            _cts = null;
        }
        cts?.Cancel();
        cts?.Dispose();
    }

    // ── Discovery loop ────────────────────────────────────────────────────────

    private async Task RunDiscoveryAsync(CancellationToken ct)
    {
        try
        {
            // Debounce: absorb file-system churn from incremental builds.
            await Task.Delay(DebounceMilliseconds, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            var (newRegistry, newHash) = await Task
                .Run(() => _discoveryService.RunDiscovery(_project, _current, _lastHash, ct), ct)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // Skip the swap if nothing changed (hash matches means RunDiscovery returned lastGood).
            if (newHash == _lastHash)
                return;

            _lastHash = newHash;
            _current  = newRegistry;

            _bindingRegistryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Normal: a newer TriggerRefresh cancelled this run.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                $"[{_project.ProjectName}] Unexpected error during binding discovery: {ex.Message}");
        }
    }
}
