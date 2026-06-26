using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Analytics;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.VisualStudio.Wizards.VsIntegration;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

// Q23: ProvideAutoLoad ensures the package loads when a solution exists, even when no
// .feature file is the foreground editor. Without this, the LSP server never starts on
// session restore if the foreground tab is a .cs file (scenario A).
[ProvideAutoLoad(
    UIContextGuids80.SolutionExists,
    PackageAutoLoadFlags.BackgroundLoad)]
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
public sealed class ReqnrollPluginPackage : AsyncPackage
{
    public const string PackageGuidString = "8d5fe503-e038-4079-9e45-697e0dcb3758";

    private static readonly TraceSource TraceSource = new("ReqnrollPluginPackage", SourceLevels.Information);
    private SynchronousFileLogger _fileLogger = null!;
    private IAnalyticsTransmitter? _analyticsTransmitter;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);

        _fileLogger = new SynchronousFileLogger();
        _fileLogger.LogInfo("ReqnrollPluginPackage: InitializeAsync started.");

        TraceSource.TraceInformation("Package initialised; waiting for solution load.");
        _fileLogger.LogInfo("Waiting for solution load...");

        // Resolve analytics transmitter for telemetry flush on shutdown.
        // Resolved early so it's available for the full package lifecycle.
        {
            var sp = await GetServiceAsync(typeof(SComponentModel)) as IServiceProvider;
            if (sp != null)
                _analyticsTransmitter = VsUtils.ResolveMefDependency<IAnalyticsTransmitter>(sp);
        }

        await WaitForSolutionLoadAsync(cancellationToken);

        // NOTE: We intentionally do NOT realize .feature stub frames here. Doing so at
        // solution load races with VS's own restore of feature tabs and spawns a second
        // LSP server process, leaving the editor bound to an unmatched server (no step
        // parameter coloring / no CodeLens usage counts). The LanguageServerProvider
        // activates the normal way (VS realizes a feature tab, or the user opens a
        // feature file), and ReqnrollLanguageClient.OnServerInitializationResultAsync
        // flushes any remaining stubs at the safe post-server-init point.
        // TODO(Q23): reinstate a non-racing/idempotent way to start the LSP when the
        // foreground tab is a .cs file and no feature file is open.

        _fileLogger.LogInfo("Solution loaded.");

        // Show the Welcome (first install) or Upgrade (version change) dialog
        // if appropriate, after a short delay so VS can finish initializing.
        await RunWelcomeServiceAsync(cancellationToken);

        _fileLogger.LogInfo("Package initialisation complete.");
        TraceSource.TraceInformation("Package initialisation complete.");
    }

    private async Task RunWelcomeServiceAsync(CancellationToken cancellationToken)
    {
        _fileLogger.LogInfo("RunWelcomeServiceAsync: starting.");
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var sp = ServiceProvider.GlobalProvider;

            // Resolve MEF services
            _fileLogger.LogInfo("RunWelcomeServiceAsync: resolving IRegistryManager...");
            var registryManager = VsUtils.ResolveMefDependency<IRegistryManager>(sp);
            if (registryManager is null)
            {
                _fileLogger.LogInfo("RunWelcomeServiceAsync: IRegistryManager not available, skipping.");
                TraceSource.TraceEvent(TraceEventType.Warning, 0,
                    "RunWelcomeService: IRegistryManager not available, skipping.");
                return;
            }
            _fileLogger.LogInfo("RunWelcomeServiceAsync: IRegistryManager resolved OK.");

            _fileLogger.LogInfo("RunWelcomeServiceAsync: resolving IVersionProvider...");
            var versionProvider = VsUtils.ResolveMefDependency<IVersionProvider>(sp);
            if (versionProvider is null)
            {
                _fileLogger.LogInfo("RunWelcomeServiceAsync: IVersionProvider not available, skipping.");
                TraceSource.TraceEvent(TraceEventType.Warning, 0,
                    "RunWelcomeService: IVersionProvider not available, skipping.");
                return;
            }
            _fileLogger.LogInfo("RunWelcomeServiceAsync: IVersionProvider resolved OK. Version=" + versionProvider.GetExtensionVersion());

            _fileLogger.LogInfo("RunWelcomeServiceAsync: resolving IFileSystemForIDE...");
            var fileSystem = VsUtils.ResolveMefDependency<IFileSystemForIDE>(sp);
            if (fileSystem is null)
            {
                _fileLogger.LogInfo("RunWelcomeServiceAsync: IFileSystemForIDE not available, skipping.");
                TraceSource.TraceEvent(TraceEventType.Warning, 0,
                    "RunWelcomeService: IFileSystemForIDE not available, skipping.");
                return;
            }
            _fileLogger.LogInfo("RunWelcomeServiceAsync: IFileSystemForIDE resolved OK.");

            _fileLogger.LogInfo("RunWelcomeServiceAsync: resolving IIdeScope...");
            var ideScope = VsUtils.ResolveMefDependency<IIdeScope>(sp);
            if (ideScope is null)
            {
                _fileLogger.LogInfo("RunWelcomeServiceAsync: IIdeScope not available, skipping.");
                TraceSource.TraceEvent(TraceEventType.Warning, 0,
                    "RunWelcomeService: IIdeScope not available, skipping.");
                return;
            }
            _fileLogger.LogInfo("RunWelcomeServiceAsync: IIdeScope resolved OK.");

            // Create the dialog service (manual creation, not MEF-exported)
            _fileLogger.LogInfo("RunWelcomeServiceAsync: resolving IVsUIShell...");
            var vsUiShell = sp.GetService(typeof(SVsUIShell)) as IVsUIShell;
            if (vsUiShell is null)
            {
                _fileLogger.LogInfo("RunWelcomeServiceAsync: IVsUIShell not available, skipping.");
                TraceSource.TraceEvent(TraceEventType.Warning, 0,
                    "RunWelcomeService: IVsUIShell not available, skipping.");
                return;
            }
            _fileLogger.LogInfo("RunWelcomeServiceAsync: IVsUIShell resolved OK.");

            var monitoringService = ideScope.MonitoringService;
            var dialogService = new VsWizardDialogService(vsUiShell, monitoringService);

            _fileLogger.LogInfo("RunWelcomeServiceAsync: creating WelcomeService...");
            var welcomeService = new WelcomeService(
                registryManager, versionProvider, dialogService, fileSystem);

            _fileLogger.LogInfo("RunWelcomeServiceAsync: calling OnIdeScopeActivityStarted...");
            welcomeService.OnIdeScopeActivityStarted(ideScope);
            _fileLogger.LogInfo("RunWelcomeServiceAsync: OnIdeScopeActivityStarted returned (dialog scheduled with 7s delay).");
        }
        catch (Exception ex)
        {
            _fileLogger.LogInfo("RunWelcomeServiceAsync: FAILED: " + ex.GetType().Name + ": " + ex.Message);
            TraceSource.TraceEvent(TraceEventType.Warning, 0,
                "RunWelcomeService: Failed: {0}", ex.Message);
        }
    }

    private async Task WaitForSolutionLoadAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
        if (solution is null)
            return;

        // Poll until the solution is open. ProvideAutoLoad(BackgroundLoad) guarantees the
        // solution exists by the time InitializeAsync runs, but projects may still be loading
        // in the background. We use a brief polling loop as a practical gate.
        const int maxAttempts = 40; // ~10 seconds
        for (int i = 0; i < maxAttempts; i++)
        {
            // The VSPSROPID_IsOpen value is 0x0000000B per the VS SDK headers.
            solution.GetProperty(0x0000000B, out var isOpen);
            if (isOpen is true)
                return;

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        }

        TraceSource.TraceInformation("WaitForSolutionLoadAsync: max attempts reached, proceeding anyway.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _analyticsTransmitter is IAsyncDisposable d)
        {
            ThreadHelper.JoinableTaskFactory.Run(() => d.DisposeAsync().AsTask());
        }
        base.Dispose(disposing);
    }
}
