using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.SDKIntegration;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspNotifications;

/// <summary>
/// Monitors DTE solution and build events and sends
/// <c>reqnroll/projectLoaded</c> / <c>reqnroll/projectUnloaded</c> notifications
/// to the LSP server via <see cref="LspInterceptingPipe"/>.
/// </summary>
/// <remarks>
/// Created by <see cref="ReqnrollLanguageClient"/> after the server has initialised
/// successfully.  Holds strong references to DTE event sinks — required because DTE
/// event objects are COM and are released if only a weak reference is held.
/// </remarks>
internal sealed class VsProjectEventMonitor : IDisposable
{
    private readonly LspInterceptingPipe _pipe;
    private readonly TraceSource         _trace;
    private readonly DTE2                _dte;
    private readonly IServiceProvider    _serviceProvider;

    // DTE event sinks — must be kept alive as fields.
    private readonly SolutionEvents _solutionEvents;
    private readonly BuildEvents    _buildEvents;

    private bool _disposed;
    private readonly CancellationTokenSource _cts = new();

    public VsProjectEventMonitor(
        LspInterceptingPipe pipe,
        TraceSource trace,
        IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _pipe            = pipe            ?? throw new ArgumentNullException(nameof(pipe));
        _trace           = trace           ?? throw new ArgumentNullException(nameof(trace));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        _dte = (DTE2)serviceProvider.GetService(typeof(DTE))
               ?? throw new InvalidOperationException("DTE service not available.");

        _solutionEvents = _dte.Events.SolutionEvents;
        _buildEvents    = _dte.Events.BuildEvents;

        _solutionEvents.ProjectAdded   += OnProjectAdded;
        _solutionEvents.ProjectRemoved += OnProjectRemoved;
        _solutionEvents.Opened         += OnSolutionOpened;
        _solutionEvents.AfterClosing   += OnSolutionClosed;
        _buildEvents.OnBuildDone       += OnBuildDone;
    }

    // ── Initial flush ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sends <c>reqnroll/projectLoaded</c> and <c>reqnroll/projectFiles</c> for every project
    /// currently in the solution.  Call once immediately after the server has initialised.
    /// </summary>
    public async Task SendInitialProjectsAsync(CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var solution = _dte.Solution;
        if (solution?.IsOpen != true)
            return;

        foreach (Project project in solution.Projects)
        {
            await TrySendProjectLoadedAsync(project, ct).ConfigureAwait(false);
            await TrySendProjectFilesAsync(project, ct).ConfigureAwait(false);
        }
    }

    // ── Scaffold notification ─────────────────────────────────────────────────

    /// <summary>
    /// Sends a <c>reqnroll/projectFiles</c> delta that registers a newly scaffolded
    /// <c>.cs</c> file in the server's membership index so Roslyn discovery accepts it.
    /// Must be called BEFORE the server receives <c>textDocument/didOpen</c> for the file.
    /// </summary>
    public async Task SendScaffoldedFileAsync(string filePath, CancellationToken ct)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var project = FindProjectContaining(filePath);
            if (project is null)
            {
                _trace.TraceEvent(TraceEventType.Warning, 0,
                    "VsProjectEventMonitor: No project found for scaffolded file '{0}'", filePath);
                return;
            }

            var tfm = VsUtils.GetTargetFrameworkMoniker(project) ?? string.Empty;
            var paramsObj = new
            {
                projectFile            = project.FullName,
                targetFrameworkMoniker = tfm,
                kind                   = 1,  // ProjectFilesKind.Delta = 1
                files                  = new[] { new { path = filePath, role = 1, added = true } }
            };
            var paramsJson = JsonConvert.SerializeObject(paramsObj, Formatting.None);
            await _pipe.SendNotificationToServerAsync("reqnroll/projectFiles", paramsJson, ct)
                       .ConfigureAwait(false);

            _trace.TraceInformation(
                "VsProjectEventMonitor: Sent projectFiles delta for scaffolded file '{0}' in '{1}'",
                Path.GetFileName(filePath), project.Name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _trace.TraceEvent(TraceEventType.Warning, 0,
                "VsProjectEventMonitor: SendScaffoldedFileAsync failed for '{0}': {1}",
                filePath, ex.Message);
        }
    }

    private Project? FindProjectContaining(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var solution = _dte.Solution;
        if (solution?.IsOpen != true)
            return null;

        Project? best    = null;
        int      bestLen = 0;
        foreach (Project project in solution.Projects)
        {
            if (!IsSolutionProject(project))
                continue;
            var folder = Path.GetDirectoryName(project.FullName) ?? string.Empty;
            if (filePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                folder.Length > bestLen)
            {
                best    = project;
                bestLen = folder.Length;
            }
        }
        return best;
    }

    // ── DTE event handlers ────────────────────────────────────────────────────

    private void OnProjectAdded(Project project)
        => FireAndForget(async ct =>
        {
            await TrySendProjectLoadedAsync(project, ct).ConfigureAwait(false);
            await TrySendProjectFilesAsync(project, ct).ConfigureAwait(false);
        });

    private void OnProjectRemoved(Project project)
        => FireAndForget(ct => TrySendProjectUnloadedAsync(project, ct));

    private void OnSolutionOpened()
        => FireAndForget(ct => SendInitialProjectsAsync(ct));

    private void OnSolutionClosed()
    {
        // Nothing to do — projects are removed individually via OnProjectRemoved before this fires.
    }

    private void OnBuildDone(vsBuildScope scope, vsBuildAction action)
    {
        // After any successful build re-send all projects so the server gets updated
        // OutputAssemblyPath values and a fresh file-membership baseline (build may have changed
        // conditional compilation/references that affect which files are included).
        if (action == vsBuildAction.vsBuildActionBuild ||
            action == vsBuildAction.vsBuildActionRebuildAll)
        {
            FireAndForget(ct => SendInitialProjectsAsync(ct));
        }
    }

    // ── Notification builders ─────────────────────────────────────────────────

    private async Task TrySendProjectLoadedAsync(Project project, CancellationToken ct)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            if (!IsSolutionProject(project))
                return;

            var paramsJson = VsProjectPayloadBuilder.BuildProjectLoadedParamsJson(
                project, GetSolutionFolder(), _serviceProvider, _trace);
            await _pipe.SendNotificationToServerAsync("reqnroll/projectLoaded", paramsJson, ct)
                       .ConfigureAwait(false);

            _trace.TraceInformation(
                "VsProjectEventMonitor: Sent projectLoaded for '{0}'", project.Name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _trace.TraceEvent(TraceEventType.Warning, 0,
                "VsProjectEventMonitor: Failed to send projectLoaded for project: {0}", ex.Message);
        }
    }

    private async Task TrySendProjectUnloadedAsync(Project project, CancellationToken ct)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            if (!IsSolutionProject(project))
                return;

            var paramsObj = new { projectFile = project.FullName };
            var paramsJson = JsonConvert.SerializeObject(paramsObj, Formatting.None);

            await _pipe.SendNotificationToServerAsync("reqnroll/projectUnloaded", paramsJson, ct)
                       .ConfigureAwait(false);

            _trace.TraceInformation(
                "VsProjectEventMonitor: Sent projectUnloaded for '{0}'", project.Name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _trace.TraceEvent(TraceEventType.Warning, 0,
                "VsProjectEventMonitor: Failed to send projectUnloaded: {0}", ex.Message);
        }
    }

    private async Task TrySendProjectFilesAsync(Project project, CancellationToken ct)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            if (!IsSolutionProject(project))
                return;

            var paramsJson = VsProjectPayloadBuilder.BuildProjectFilesParamsJson(project, _trace);
            await _pipe.SendNotificationToServerAsync("reqnroll/projectFiles", paramsJson, ct)
                       .ConfigureAwait(false);

            _trace.TraceInformation(
                "VsProjectEventMonitor: Sent projectFiles baseline for '{0}'", project.Name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _trace.TraceEvent(TraceEventType.Warning, 0,
                "VsProjectEventMonitor: Failed to send projectFiles for '{0}': {1}",
                project.Name, ex.Message);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GetSolutionFolder()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var solutionFile = _dte.Solution?.FullName;
        return string.IsNullOrEmpty(solutionFile)
            ? string.Empty
            : Path.GetDirectoryName(solutionFile) ?? string.Empty;
    }

    private static bool IsSolutionProject(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return VsUtils.IsSolutionProject(project);
    }

    private void FireAndForget(Func<CancellationToken, Task> action)
    {
        var ct = _cts.Token;
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await action(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _trace.TraceEvent(TraceEventType.Warning, 0,
                    "VsProjectEventMonitor: Background task failed: {0}", ex.Message);
            }
        });
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();

        _solutionEvents.ProjectAdded   -= OnProjectAdded;
        _solutionEvents.ProjectRemoved -= OnProjectRemoved;
        _solutionEvents.Opened         -= OnSolutionOpened;
        _solutionEvents.AfterClosing   -= OnSolutionClosed;
        _buildEvents.OnBuildDone       -= OnBuildDone;
    }
}
