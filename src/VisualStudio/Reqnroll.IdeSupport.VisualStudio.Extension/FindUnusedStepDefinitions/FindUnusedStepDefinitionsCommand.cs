#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Reqnroll.IdeSupport.Common.Diagnostics;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// "Find Unused Step Definitions" command placed in the Reqnroll submenu under Extensions.
/// Unlike F14, this command is not scoped to a C# editor — it is a workspace-wide operation
/// available whenever the server is running.
/// </summary>
[VisualStudioContribution]
internal sealed class FindUnusedStepDefinitionsCommand : Command
{
    private readonly FindUnusedStepDefinitionsState _state;
    private readonly TraceSource _traceSource;
    private readonly IDeveroomLogger _fileLogger = new SynchronousFileLogger();

    public FindUnusedStepDefinitionsCommand(
        FindUnusedStepDefinitionsState state,
        TraceSource traceSource)
    {
        _state       = state;
        _traceSource = traceSource;
    }

    public override CommandConfiguration CommandConfiguration => new("Find Unused Step Definitions")
    {
        Icon = new CommandIconConfiguration(ImageMoniker.Custom("ReqnrollIcon"), IconSettings.IconAndText),
        // No VisibleWhen constraint — available in any context once the server is running.
        Placements = [],  // Placed via ReqnrollMenu only; no context-menu placement for this command.
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            _fileLogger.LogInfo("FindUnusedStepDefinitionsCommand: invoked.");

            var service  = _state.Service;
            var renderer = _state.Renderer;
            if (service is null || renderer is null)
            {
                _fileLogger.LogWarning(
                    "FindUnusedStepDefinitionsCommand: LSP server not yet initialized " +
                    $"(service={(service is null ? "null" : "set")}, " +
                    $"renderer={(renderer is null ? "null" : "set")}).");
                return;
            }

            var result = await service.FindUnusedAsync(cancellationToken).ConfigureAwait(false);

            _fileLogger.LogInfo(
                $"FindUnusedStepDefinitionsCommand: {result.Items.Count} unused step definition(s).");

            await renderer.RenderAsync(result, cancellationToken).ConfigureAwait(false);

            _fileLogger.LogInfo("FindUnusedStepDefinitionsCommand: render complete.");
        }
        catch (Exception ex)
        {
            _fileLogger.LogWarning($"FindUnusedStepDefinitionsCommand: failed: {ex}");
            _traceSource.TraceEvent(TraceEventType.Error, 0,
                "FindUnusedStepDefinitionsCommand: failed: {0}", ex);
        }
    }
}
