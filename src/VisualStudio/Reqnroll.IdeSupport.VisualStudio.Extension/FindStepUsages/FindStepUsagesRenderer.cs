#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.FindAllReferences;
using Microsoft.VisualStudio.Shell.TableManager;
using Reqnroll.IdeSupport.Common.Diagnostics;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

/// <summary>
/// Opens the VS Find All References tool window and populates it with
/// <see cref="StepUsagesResult"/> locations (F14 P3b).
/// Must be constructed on any thread but <see cref="RenderAsync"/> switches to the
/// UI thread internally before calling VS services.
/// </summary>
internal sealed class FindStepUsagesRenderer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TraceSource      _traceSource;
    // TraceSource is not routed to the shared reqnroll-vs-debug-*.log; mirror the key
    // diagnostics there so a failure to open the window is visible in a single run.
    private readonly IDeveroomLogger  _fileLogger = new SynchronousFileLogger();

    public FindStepUsagesRenderer(IServiceProvider serviceProvider, TraceSource traceSource)
    {
        _serviceProvider = serviceProvider;
        _traceSource     = traceSource;
    }

    /// <summary>
    /// Opens the Find All References window with <paramref name="label"/> as the title,
    /// then pushes all locations from <paramref name="result"/> into it.
    /// Silently does nothing if the VS service is unavailable.
    /// </summary>
    public async Task RenderAsync(
        string            label,
        StepUsagesResult  result,
        CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var far = _serviceProvider.GetService(typeof(SVsFindAllReferences)) as IFindAllReferencesService;
        if (far is null)
        {
            _traceSource.TraceEvent(TraceEventType.Warning, 0,
                "FindStepUsagesRenderer: IFindAllReferencesService not available");
            _fileLogger.LogWarning("FindStepUsagesRenderer: IFindAllReferencesService not available.");
            return;
        }

        var window = far.StartSearch(label);
        if (window is null)
        {
            _traceSource.TraceEvent(TraceEventType.Warning, 0,
                "FindStepUsagesRenderer: StartSearch returned null window");
            _fileLogger.LogWarning("FindStepUsagesRenderer: StartSearch returned null window.");
            return;
        }

        var dataSource = new FeatureReferencesDataSource(result.Locations);
        window.Manager.AddSource(dataSource,
            StandardTableKeyNames.DocumentName,
            StandardTableKeyNames.Line,
            StandardTableKeyNames.Column,
            StandardTableKeyNames.Text,
            StandardTableKeyNames.ProjectName,
            "description");

        _traceSource.TraceInformation(
            "FindStepUsagesRenderer: opened FAR window '{0}' with {1} location(s)",
            label, result.Locations.Count);
        _fileLogger.LogInfo(
            $"FindStepUsagesRenderer: opened FAR window '{label}' with {result.Locations.Count} location(s).");
    }
}
