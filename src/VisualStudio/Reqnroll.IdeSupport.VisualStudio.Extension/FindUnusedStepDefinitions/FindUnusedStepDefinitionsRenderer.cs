#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.FindAllReferences;
using Microsoft.VisualStudio.Shell.TableManager;
using Reqnroll.IdeSupport.Common.Diagnostics;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// Opens the VS Find All References tool window and populates it with
/// <see cref="UnusedStepDefinitionsResult"/> locations (F15).
/// </summary>
internal sealed class FindUnusedStepDefinitionsRenderer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TraceSource      _traceSource;
    private readonly IDeveroomLogger  _fileLogger = new SynchronousFileLogger();

    public FindUnusedStepDefinitionsRenderer(IServiceProvider serviceProvider, TraceSource traceSource)
    {
        _serviceProvider = serviceProvider;
        _traceSource     = traceSource;
    }

    public async Task RenderAsync(
        UnusedStepDefinitionsResult result,
        CancellationToken           cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var far = _serviceProvider.GetService(typeof(SVsFindAllReferences)) as IFindAllReferencesService;
        if (far is null)
        {
            _traceSource.TraceEvent(TraceEventType.Warning, 0,
                "FindUnusedStepDefinitionsRenderer: IFindAllReferencesService not available");
            _fileLogger.LogWarning(
                "FindUnusedStepDefinitionsRenderer: IFindAllReferencesService not available.");
            return;
        }

        var count = result.Items.Count;
        var label = count == 0
            ? "Reqnroll: 0 unused step definitions"
            : $"Reqnroll: {count} unused step definition{(count == 1 ? "" : "s")}";

        var window = far.StartSearch(label);
        if (window is null)
        {
            _traceSource.TraceEvent(TraceEventType.Warning, 0,
                "FindUnusedStepDefinitionsRenderer: StartSearch returned null window");
            _fileLogger.LogWarning(
                "FindUnusedStepDefinitionsRenderer: StartSearch returned null window.");
            return;
        }

        var dataSource = new UnusedStepDefinitionsDataSource(result.Items);
        window.Manager.AddSource(dataSource,
            StandardTableKeyNames.Text,           // "ClassName.MethodName  ·  Expression" in Code column
            StandardTableKeyNames.DocumentName,
            StandardTableKeyNames.Line,
            StandardTableKeyNames.Column,
            StandardTableKeyNames.ProjectName);
        // Note: StandardTableKeyNames.Definition is NOT declared here.  The VS FAR window
        // type-checks its value for a Roslyn DefinitionBucket; plain strings are ignored and
        // produce "[Definition:Unknown]".  "description" is also omitted — declaring it causes
        // VS to auto-generate a Description column that duplicates the Code column text.

        _traceSource.TraceInformation(
            "FindUnusedStepDefinitionsRenderer: opened FAR window '{0}' with {1} item(s)",
            label, count);
        _fileLogger.LogInfo(
            $"FindUnusedStepDefinitionsRenderer: opened FAR window '{label}' with {count} item(s).");
    }
}
