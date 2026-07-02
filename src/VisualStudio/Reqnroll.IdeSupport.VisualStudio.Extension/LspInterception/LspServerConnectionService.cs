using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Nerdbank.Streams;
using Reqnroll.IdeSupport.Common.Analytics;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.VisualStudio.Extension.Classification;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspNotifications;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Owns the lifetime of the out-of-proc Reqnroll LSP server process and the
/// <see cref="LspInterceptingPipe"/> that sits between it and VS.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a DI singleton (see <c>ExtensionEntrypoint.InitializeServices</c>) and
/// constructor-injected into <see cref="ReqnrollLanguageClient"/>. VS.Extensibility constructs
/// <c>ReqnrollLanguageClient</c> when the extension loads — to read its
/// <c>LanguageServerProviderConfiguration</c> document filter — well before any <c>.feature</c>
/// file is opened. Injecting this service there is enough to trigger process launch and pipe
/// construction immediately, off the document-open path: this class's constructor starts the
/// work eagerly and caches the resulting task, so
/// <see cref="ReqnrollLanguageClient.CreateServerConnectionAsync"/> (invoked later, on first
/// matching document) just awaits an already-in-flight or already-completed task via
/// <see cref="GetConnectionAsync"/> instead of paying launch latency on that path.
/// </para>
/// <para>
/// <b>Known limitation:</b> <see cref="GetConnectionAsync"/> hands out the same cached pipe on
/// every call. If VS activates the provider more than once in a session — the still-open
/// multi-tab-restore duplicate-server race (see project memory "vs-package-duplicate-server-q23")
/// — a second caller gets the same (already-consumed) pipe rather than a fresh process, which is
/// different from (not necessarily better or worse than) the pre-existing behaviour of spinning up
/// a second server. This is a deliberate scope boundary: solving the duplicate-activation race is
/// tracked separately and was out of scope for making startup eager.
/// </para>
/// </remarks>
internal sealed class LspServerConnectionService : IDisposable
{
    private readonly TraceSource _traceSource;
    private readonly IDeveroomLogger _fileLogger;
    private readonly StepCodeLensState _stepCodeLensState;

    // JoinableTask (not a plain Task) so GetConnectionAsync's await is JTF-aware — avoids the
    // VSTHRD003 "awaiting a foreign task" analyzer error for a task started outside the awaiting
    // method's own async context. StartAsync itself never touches the UI thread.
    private readonly Microsoft.VisualStudio.Threading.JoinableTask<IDuplexPipe?> _startTask;

    private Process? _serverProcess;
    private LspInspectorLogger? _inspectorLogger;
    private LspInterceptingPipe? _interceptingPipe;
    private ChildProcessJob? _childJob;
    private bool _disposed;

    public LspServerConnectionService(TraceSource traceSource, StepCodeLensState stepCodeLensState)
    {
        _traceSource       = traceSource       ?? throw new ArgumentNullException(nameof(traceSource));
        _stepCodeLensState = stepCodeLensState ?? throw new ArgumentNullException(nameof(stepCodeLensState));
        _fileLogger        = new SynchronousFileLogger();

        _traceSource.TraceInformation("LspServerConnectionService: Instance created — starting server eagerly.");
        _fileLogger.LogInfo("LspServerConnectionService: Instance created — starting server eagerly.");

        // Fire off immediately; not awaited here. Consumers (ReqnrollLanguageClient) await
        // GetConnectionAsync() whenever they're ready, which may be well after this completes.
        _startTask = ThreadHelper.JoinableTaskFactory.RunAsync(StartAsync);
    }

    /// <summary>
    /// The intercepting pipe once started; <c>null</c> until the server process and pipe have
    /// been constructed. Used by components (e.g. <see cref="VsProjectEventMonitor"/>) that need
    /// to send notifications directly to the server, bypassing VS.
    /// </summary>
    public LspInterceptingPipe? InterceptingPipe => _interceptingPipe;

    /// <summary>
    /// Set by <see cref="ReqnrollLanguageClient"/> once the MEF-resolved analytics transmitter is
    /// available (post-init, main thread). Read lazily by <see cref="TelemetryEventInterceptor"/>,
    /// which is constructed before this is known.
    /// </summary>
    public IAnalyticsTransmitter? AnalyticsTransmitter { get; set; }

    /// <summary>
    /// Set by <see cref="ReqnrollLanguageClient"/> once the project monitor is constructed
    /// (post-init, main thread — requires DTE). Read lazily by
    /// <see cref="ScaffoldTrackingInterceptor"/>, which is constructed before this is known.
    /// </summary>
    public VsProjectEventMonitor? ProjectMonitor { get; set; }

    /// <summary>
    /// Awaits the (already-started) server process and pipe construction.
    /// </summary>
    /// <returns>The VS-facing <see cref="IDuplexPipe"/>, or <c>null</c> if startup failed.</returns>
    public Task<IDuplexPipe?> GetConnectionAsync() => _startTask.JoinAsync();

    /// <summary>
    /// Resolves the bundled LSP server executable path relative to the extension assembly's own
    /// location. Pure/deterministic — extracted so the path-building logic is unit-testable
    /// without touching <see cref="Process"/> or <see cref="ThreadHelper"/>.
    /// </summary>
    internal static string ResolveServerExePath(string extensionAssemblyLocation)
        => Path.Combine(
            Path.GetDirectoryName(extensionAssemblyLocation)!,
            "LSPServer",
            "Reqnroll.IdeSupport.LSP.Server.exe");

    private async Task<IDuplexPipe?> StartAsync()
    {
        var serverExe = ResolveServerExePath(typeof(LspServerConnectionService).Assembly.Location);

        _traceSource.TraceInformation("LspServerConnectionService: Starting server. Server exe path: {0}", serverExe);
        _fileLogger.LogInfo($"LspServerConnectionService: Starting server — server exe: {serverExe}");

        if (!File.Exists(serverExe))
        {
            _traceSource.TraceEvent(TraceEventType.Error, 0,
                "LspServerConnectionService: Server executable not found at '{0}'.", serverExe);
            _fileLogger.LogWarning($"LspServerConnectionService: Server executable not found: {serverExe}");
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo(serverExe)
            {
                UseShellExecute        = false,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                // Tell the LSP server which IDE is connecting so it selects the correct
                // semantic token profile (legend + DeveroomTag→token type mapping).
                Arguments              = "--ide visualstudio",
            };

            _serverProcess = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");

            // Fire-and-forget: pushes project/discovery data to the server's preload side
            // channel as soon as the solution is loaded, well before VS's own initialize
            // handshake (and hence CreateServerConnectionAsync) may happen. Must not be awaited
            // here — it can take up to ~60s (waiting for solution load) and must not delay
            // returning the pipe to VS. See LspProjectPreloadPusher's remarks.
            _ = LspProjectPreloadPusher.PushAsync(_serverProcess.Id, _traceSource, CancellationToken.None);

            // Assign to a kill-on-close Job Object so the server is terminated by the OS
            // when this VS process exits, even if Dispose is never called.
            try
            {
                _childJob = new ChildProcessJob();
                _childJob.AddProcess(_serverProcess);
            }
            catch (Exception ex)
            {
                _traceSource.TraceEvent(TraceEventType.Warning, 0,
                    "LspServerConnectionService: Could not assign server to Job Object: {0}", ex.Message);
            }

            _serverProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    _traceSource.TraceEvent(TraceEventType.Warning, 0, "LSPServer stderr: {0}", e.Data);
            };
            _serverProcess.BeginErrorReadLine();

            _traceSource.TraceInformation("LspServerConnectionService: Server process started (PID {0}).", _serverProcess.Id);

            IDuplexPipe rawPipe = new DuplexPipe(
                _serverProcess.StandardOutput.BaseStream.UsePipeReader(),
                _serverProcess.StandardInput.BaseStream.UsePipeWriter());

            // Build the LSP Inspector log file path, unique per session.
            var logDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reqnroll");
            var logFile = Path.Combine(logDir, $"reqnroll-vs-inspector-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _fileLogger.LogInfo($"LspServerConnectionService: Server process started (PID {_serverProcess.Id}). Inspector log: {logFile}");
            _inspectorLogger = new LspInspectorLogger(logFile, _traceSource);

            // Observes semanticTokens traffic (both directions) and caches the decoded tokens so the
            // editor classifier can colour .feature files with Reqnroll's custom classifications,
            // bypassing VS's fixed built-in token-type→classification table. One instance is shared
            // by both pipelines so it sees requests (VS→Server) and their responses (Server→VS).
            var semanticTokensInterceptor = new SemanticTokensClassificationInterceptor(
                SemanticTokenClassificationStore.Instance, _traceSource);

            // Tracks .cs files created by the scaffold code action and injects a
            // reqnroll/projectFiles delta before the server sees textDocument/didOpen.
            // Uses a lazy reference because ProjectMonitor is set well after the pipe exists.
            var scaffoldInterceptor = new ScaffoldTrackingInterceptor(
                () => ProjectMonitor, _traceSource);

            // Watches textDocument/didChange on .cs files and invalidates code lenses
            // so VS re-queries the server for updated usage counts after a binding edit.
            var codeLensRefreshInterceptor = new CodeLensRefreshInterceptor(
                _stepCodeLensState, _traceSource);

            // Send pipeline:   VS → [logger, semanticTokens, scaffold, codeLensRefresh] → Server
            // Receive pipeline: Server → [logger, semanticTokens, scaffold, codeLensRefresh, telemetry] → VS
            // codeLensRefresh is on both pipelines: send watches .cs didChange; receive watches the
            // server's reqnroll/refreshCodeLens push after a full registry replacement.
            var sendInterceptors = new ILspMessageInterceptor[]
                { _inspectorLogger, semanticTokensInterceptor, scaffoldInterceptor, codeLensRefreshInterceptor };

            // Telemetry interceptor: lazy reference because AnalyticsTransmitter is resolved
            // from MEF on the main thread during OnServerInitializationResultAsync.
            var telemetryInterceptor = new TelemetryEventInterceptor(() => AnalyticsTransmitter, _traceSource);
            var receiveInterceptors = new ILspMessageInterceptor[]
                { _inspectorLogger, semanticTokensInterceptor, scaffoldInterceptor, codeLensRefreshInterceptor, telemetryInterceptor };

            _interceptingPipe = new LspInterceptingPipe(rawPipe, sendInterceptors, receiveInterceptors, _traceSource);
            // Pass CancellationToken.None: the pumps must live for the entire connection
            // lifetime, not just for the duration of this async creation call. The pipe's
            // own internal CTS (cancelled in Dispose) provides the shutdown signal.
            await _interceptingPipe.StartAsync(CancellationToken.None).ConfigureAwait(false);

            return _interceptingPipe.VsFacingPipe;
        }
        catch (Exception ex)
        {
            _traceSource.TraceEvent(TraceEventType.Error, 0,
                "LspServerConnectionService: Failed to start server: {0}", ex);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _fileLogger.LogInfo("LspServerConnectionService: Disposing — shutting down server connection.");

        // ProjectMonitor is disposed by ReqnrollLanguageClient.Dispose (UI-thread-bound, COM event
        // unsubscription) whenever the provider deactivates — not here, since this service's Dispose
        // may run off the UI thread at extension unload and VsProjectEventMonitor.Dispose() asserts
        // ThreadHelper.ThrowIfNotOnUIThread(). It is set to null there too.

        _interceptingPipe?.Dispose();
        _interceptingPipe = null;

        _inspectorLogger?.Dispose();
        _inspectorLogger = null;

        try { _serverProcess?.Kill(); } catch { /* best-effort */ }
        _serverProcess?.Dispose();
        _serverProcess = null;

        _childJob?.Dispose();
        _childJob = null;
    }
}
