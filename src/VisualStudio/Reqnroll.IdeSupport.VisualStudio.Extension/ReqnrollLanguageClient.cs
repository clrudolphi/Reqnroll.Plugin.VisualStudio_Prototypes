using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.LanguageServer;
using Microsoft.VisualStudio.RpcContracts.LanguageServerProvider;
using Microsoft.VisualStudio.Shell;
using Reqnroll.IdeSupport.Common.Analytics;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.VisualStudio.Extension.CommentToggle;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspNotifications;
#pragma warning disable VSEXTPREVIEW_LSP

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

[VisualStudioContribution]
internal class ReqnrollLanguageClient : LanguageServerProvider
{
    private readonly TraceSource _traceSource;
    private readonly IDeveroomLogger _fileLogger;
    private readonly FindStepUsagesState _findStepUsagesState;
    private readonly FindUnusedStepDefinitionsState _findUnusedStepDefinitionsState;
    private readonly GoToHooksState _goToHooksState;
    private readonly StepCodeLensState _stepCodeLensState;
    private readonly CommentToggleState _commentToggleState;
    private readonly RenameStepState _renameStepState;
    private readonly LspServerConnectionService _connectionService;

    public ReqnrollLanguageClient(
        ExtensionCore container,
        VisualStudioExtensibility extensibilityObject,
        TraceSource traceSource,
        FindStepUsagesState findStepUsagesState,
        FindUnusedStepDefinitionsState findUnusedStepDefinitionsState,
        GoToHooksState goToHooksState,
        StepCodeLensState stepCodeLensState,
        CommentToggleState commentToggleState,
        RenameStepState renameStepState,
        LspServerConnectionService connectionService)
        : base(container, extensibilityObject)
    {
        _traceSource                    = traceSource;
        _findStepUsagesState            = findStepUsagesState;
        _findUnusedStepDefinitionsState = findUnusedStepDefinitionsState;
        _goToHooksState                 = goToHooksState;
        _stepCodeLensState              = stepCodeLensState;
        _commentToggleState             = commentToggleState;
        _renameStepState                = renameStepState;
        // Constructor-injecting LspServerConnectionService is what makes server startup eager:
        // VS.Extensibility constructs this class (to read LanguageServerProviderConfiguration)
        // well before any .feature file is opened, which resolves the singleton service and
        // starts its constructor's fire-and-forget process launch immediately. See
        // LspServerConnectionService's remarks for the full rationale.
        _connectionService   = connectionService;
        _fileLogger          = new SynchronousFileLogger();
        _traceSource.TraceInformation("ReqnrollLanguageClient: Instance created.");
        _fileLogger.LogInfo(
            $"ReqnrollLanguageClient: VS extension loaded. " +
            $"Assembly: {typeof(ReqnrollLanguageClient).Assembly.Location}");
    }

    /// <inheritdoc />
    public override LanguageServerProviderConfiguration LanguageServerProviderConfiguration =>
        new("Reqnroll Language Client",
            new[]
            {
                DocumentFilter.FromDocumentType(GherkinDocumentType.GherkinDocument),
            });

    /// <inheritdoc />
    public override async Task<IDuplexPipe?> CreateServerConnectionAsync(CancellationToken cancellationToken)
    {
        _traceSource.TraceInformation("ReqnrollLanguageClient: CreateServerConnectionAsync called — awaiting eager connection.");
        _fileLogger.LogInfo("ReqnrollLanguageClient: CreateServerConnectionAsync — awaiting eager connection.");

        // Startup (process launch + pipe construction) was kicked off eagerly when
        // LspServerConnectionService was constructed — see its remarks. This just awaits
        // whatever is already in flight (or already completed) rather than starting it now.
        var pipe = await _connectionService.GetConnectionAsync().ConfigureAwait(false);

        if (pipe is null)
        {
            _traceSource.TraceEvent(TraceEventType.Error, 0,
                "ReqnrollLanguageClient: LSP server connection unavailable. Disabling.");
            _fileLogger.LogWarning("ReqnrollLanguageClient: LSP server connection unavailable. Disabling.");
            Enabled = false;
            return null;
        }

        return pipe;
    }

    /// <inheritdoc />
    public override async Task OnServerInitializationResultAsync(
        ServerInitializationResult serverInitializationResult,
        LanguageServerInitializationFailureInfo? initializationFailureInfo,
        CancellationToken cancellationToken)
    {
        if (serverInitializationResult == ServerInitializationResult.Failed)
        {
            var failMsg = initializationFailureInfo?.StatusMessage
                          ?? initializationFailureInfo?.Exception?.Message
                          ?? "(none)";
            _traceSource.TraceEvent(TraceEventType.Error, 0,
                "ReqnrollLanguageClient: Server initialization failed. Info: {0}", failMsg);
            _fileLogger.LogWarning($"ReqnrollLanguageClient: Server initialization failed: {failMsg}");
            Enabled = false;
            return;
        }

        _traceSource.TraceInformation(
            "ReqnrollLanguageClient: Server initialized successfully ({0}).",
            serverInitializationResult);
        _fileLogger.LogInfo($"ReqnrollLanguageClient: Server initialized successfully ({serverInitializationResult}).");

        // Start monitoring VS project events and flush the current solution state.
        var interceptingPipe = _connectionService.InterceptingPipe;
        if (interceptingPipe is not null)
        {
            // GoToHooksService and FindStepUsagesService use only
            // LspInterceptingPipe + TraceSource — no COM, safe here.
            _findStepUsagesState.Service            = new FindStepUsagesService(interceptingPipe, _traceSource);
            _findUnusedStepDefinitionsState.Service = new FindUnusedStepDefinitionsService(interceptingPipe, _traceSource);
            _goToHooksState.Service                 = new GoToHooksService(interceptingPipe, _traceSource);
            _stepCodeLensState.Service              = new StepCodeLensService(interceptingPipe, _traceSource);
            _commentToggleState.Service             = new CommentToggleService(interceptingPipe, _traceSource);
            _renameStepState.Service                 = new RenameStepService(interceptingPipe, _traceSource);

            // Set the VSSDK command filter redirect so the keyboard shortcut interception
            // for Edit.CommentSelection/UncommentSelection/ToggleLineComment calls our service.
            CommentToggleRedirect.ToggleCommentAsync = _commentToggleState.Service.ToggleCommentAsync;

            try
            {
                // VsProjectEventMonitor and FindStepUsagesRenderer both access VS COM services
                // (DTE, SVsFindAllReferences).  VS.Extensibility may call this method on a
                // background thread (e.g. the JSON-RPC receive thread), so we marshal explicitly.
                await ThreadHelper.JoinableTaskFactory
                    .SwitchToMainThreadAsync(cancellationToken);

                var serviceProvider = ServiceProvider.GlobalProvider;
                _connectionService.AnalyticsTransmitter = ResolveMefService<IAnalyticsTransmitter>(serviceProvider);
                _traceSource.TraceInformation(
                    "ReqnrollLanguageClient: IAnalyticsTransmitter resolved: {0}",
                    _connectionService.AnalyticsTransmitter is not null ? "yes" : "no");
                _findStepUsagesState.Renderer            = new FindStepUsagesRenderer(serviceProvider, _traceSource);
                _findUnusedStepDefinitionsState.Renderer = new FindUnusedStepDefinitionsRenderer(serviceProvider, _traceSource);

                // F18 — reuse F14 find-usages components for the code-lens click action.
                _stepCodeLensState.FindUsagesService  = _findStepUsagesState.Service;
                _stepCodeLensState.FindUsagesRenderer = _findStepUsagesState.Renderer;

                _fileLogger.LogInfo("ReqnrollLanguageClient: Creating VsProjectEventMonitor.");
                _connectionService.ProjectMonitor = new VsProjectEventMonitor(
                    interceptingPipe, _traceSource, serviceProvider);

                _fileLogger.LogInfo("ReqnrollLanguageClient: Sending initial projects.");
                await _connectionService.ProjectMonitor
                    .SendInitialProjectsAsync(cancellationToken)
                    .ConfigureAwait(false);

                _fileLogger.LogInfo("ReqnrollLanguageClient: Flushing .feature stub frames.");
                await VsStubFrameInitializer.ForceInitFeatureStubsAsync(
                        ServiceProvider.GlobalProvider, _traceSource, cancellationToken)
                    .ConfigureAwait(false);

                _fileLogger.LogInfo("ReqnrollLanguageClient: Initial project flush complete.");
            }
            catch (Exception ex)
            {
                _traceSource.TraceEvent(TraceEventType.Warning, 0,
                    "ReqnrollLanguageClient: Could not start project monitor: {0}", ex.Message);
                _fileLogger.LogWarning(
                    $"ReqnrollLanguageClient: Could not start project monitor: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool isDisposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (isDisposing)
        {
            _fileLogger.LogInfo("ReqnrollLanguageClient: Disposing — shutting down server connection.");

            // ProjectMonitor is UI-thread/COM-bound, so it is disposed here (this method already
            // asserts the UI thread above) rather than in LspServerConnectionService.Dispose.
            _connectionService.ProjectMonitor?.Dispose();
            _connectionService.ProjectMonitor = null;

            _findStepUsagesState.Service             = null;
            _findStepUsagesState.Renderer            = null;
            _findUnusedStepDefinitionsState.Service  = null;
            _findUnusedStepDefinitionsState.Renderer = null;
            _goToHooksState.Service                  = null;
            _stepCodeLensState.Service           = null;
            _stepCodeLensState.FindUsagesService  = null;
            _stepCodeLensState.FindUsagesRenderer = null;
            _commentToggleState.Service = null;
            _renameStepState.Service = null;

            // _connectionService itself is NOT disposed here: it's a DI-owned singleton whose
            // lifetime spans the whole extension session, not just this provider instance. The DI
            // container disposes it (tearing down the server process/pipe) on extension unload.
        }

        base.Dispose(isDisposing);
    }

    // ── MEF resolution helper ──────────────────────────────────────────────

    /// <summary>
    /// Resolves a MEF-exported service from the VS component model.
    /// </summary>
    private static T? ResolveMefService<T>(IServiceProvider serviceProvider) where T : class
    {
        try
        {
            var componentModel = (IComponentModel)serviceProvider.GetService(typeof(SComponentModel));
            return componentModel?.GetService<T>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "ReqnrollLanguageClient: Failed to resolve MEF service {0}: {1}",
                typeof(T).Name, ex.Message);
            return null;
        }
    }
}
