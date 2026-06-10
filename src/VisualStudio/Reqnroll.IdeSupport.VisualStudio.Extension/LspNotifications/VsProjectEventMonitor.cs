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

    // ── DTE event handlers ────────────────────────────────────────────────────

    private void OnProjectAdded(Project project)
        => FireAndForget(async () =>
        {
            await TrySendProjectLoadedAsync(project, CancellationToken.None).ConfigureAwait(false);
            await TrySendProjectFilesAsync(project, CancellationToken.None).ConfigureAwait(false);
        });

    private void OnProjectRemoved(Project project)
        => FireAndForget(() => TrySendProjectUnloadedAsync(project, CancellationToken.None));

    private void OnSolutionOpened()
        => FireAndForget(() => SendInitialProjectsAsync(CancellationToken.None));

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
            FireAndForget(() => SendInitialProjectsAsync(CancellationToken.None));
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

            var projectFile   = project.FullName;
            var projectFolder = Path.GetDirectoryName(projectFile) ?? string.Empty;
            var workspaceFolder = GetSolutionFolder();

            var outputAssemblyPath = VsUtils.GetOutputAssemblyPath(project) ?? string.Empty;
            var tfm = VsUtils.GetTargetFrameworkMoniker(project) ?? string.Empty;

            var packageRefs = GetPackageReferences(project);

            var paramsObj = new
            {
                workspaceFolder,
                projectFile,
                projectFolder,
                outputAssemblyPath,
                targetFrameworkMoniker = tfm,
                packageReferences = packageRefs
            };

            var paramsJson = JsonConvert.SerializeObject(paramsObj, Formatting.None);
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

            var tfm     = VsUtils.GetTargetFrameworkMoniker(project) ?? string.Empty;
            var entries = BuildProjectFileEntries(project);

            var paramsObj = new
            {
                projectFile            = project.FullName,
                targetFrameworkMoniker = tfm,
                kind                   = 0,      // ProjectFilesKind.Baseline = 0
                files                  = entries,
            };

            var paramsJson = JsonConvert.SerializeObject(paramsObj, Formatting.None);
            await _pipe.SendNotificationToServerAsync("reqnroll/projectFiles", paramsJson, ct)
                       .ConfigureAwait(false);

            _trace.TraceInformation(
                "VsProjectEventMonitor: Sent projectFiles baseline for '{0}' ({1} file(s))",
                project.Name, entries.Length);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _trace.TraceEvent(TraceEventType.Warning, 0,
                "VsProjectEventMonitor: Failed to send projectFiles for '{0}': {1}",
                project.Name, ex.Message);
        }
    }

    private object[] BuildProjectFileEntries(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var entries = new List<object>();
            // DTE can surface the same file on more than one path (e.g. a generated .feature.cs is
            // nested under its .feature via DependentUpon, causing the feature node to be walked
            // twice).  Deduplicate by full path so the server receives each file once.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in VsUtils.GetPhysicalFileProjectItems(project))
            {
                var path = VsUtils.GetFilePath(item);
                if (path is null)
                    continue;

                int role;
                var ext = Path.GetExtension(path);
                if (ext.Equals(".feature", StringComparison.OrdinalIgnoreCase))
                    role = 0;   // ProjectFileRole.Feature = 0
                else if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                    role = 1;   // ProjectFileRole.Binding = 1
                else
                    continue;

                if (!seen.Add(path))
                    continue;

                entries.Add(new { path, role, added = true });
            }
            return entries.ToArray();
        }
        catch (Exception ex)
        {
            _trace.TraceEvent(TraceEventType.Warning, 0,
                "VsProjectEventMonitor: Could not enumerate project items for '{0}': {1}",
                project.Name, ex.Message);
            return Array.Empty<object>();
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

    private object[] GetPackageReferences(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return VsUtils.GetInstalledNuGetPackages(_serviceProvider, project.FullName)
                .Select(p => (object)new
                {
                    packageId   = p.Id,
                    version     = p.Version,
                    installPath = p.InstallPath ?? string.Empty
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            _trace.TraceEvent(TraceEventType.Warning, 0,
                "VsProjectEventMonitor: Could not read NuGet packages for '{0}': {1}",
                project.Name, ex.Message);
            return Array.Empty<object>();
        }
    }

    private void FireAndForget(Func<Task> action)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
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

        _solutionEvents.ProjectAdded   -= OnProjectAdded;
        _solutionEvents.ProjectRemoved -= OnProjectRemoved;
        _solutionEvents.Opened         -= OnSolutionOpened;
        _solutionEvents.AfterClosing   -= OnSolutionClosed;
        _buildEvents.OnBuildDone       -= OnBuildDone;
    }
}
