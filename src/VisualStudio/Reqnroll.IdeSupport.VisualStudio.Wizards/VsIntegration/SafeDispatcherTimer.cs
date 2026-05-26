// Ported from Reqnroll.VisualStudio\SafeDispatcherTimer.cs
// Only used by VsSimulatedItemAddWizardBase — kept in VsIntegration because
// it has a hard WPF (DispatcherTimer / MessageBox) dependency.
using Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.VsIntegration;

public class SafeDispatcherTimer
{
    private readonly Func<bool> _action;
    private readonly DispatcherTimer _dispatcherTimer;
    private readonly IDeveroomLogger? _logger;
    private readonly IWizardTelemetryLogger? _monitoringService;

    private SafeDispatcherTimer(int intervalSeconds, IDeveroomLogger? logger, IWizardTelemetryLogger? monitoringService,
        Action action)
    {
        _action = () => { action(); return false; };
        _logger = logger;
        _monitoringService = monitoringService;
        _dispatcherTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(intervalSeconds),
            DispatcherPriority.ContextIdle,
            DispatcherTick,
            Dispatcher.CurrentDispatcher);
    }

    private SafeDispatcherTimer(int intervalSeconds, IDeveroomLogger? logger, IWizardTelemetryLogger? monitoringService,
        Func<bool> action)
    {
        _action = action;
        _logger = logger;
        _monitoringService = monitoringService;
        _dispatcherTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(intervalSeconds),
            DispatcherPriority.ContextIdle,
            DispatcherTick,
            Dispatcher.CurrentDispatcher);
    }

    public static SafeDispatcherTimer CreateOneTime(int intervalSeconds, IDeveroomLogger? logger,
        IWizardTelemetryLogger? monitoringService, Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        return new SafeDispatcherTimer(intervalSeconds, logger, monitoringService, action);
    }

    public static SafeDispatcherTimer CreateContinuing(int intervalSeconds, IDeveroomLogger? logger,
        IWizardTelemetryLogger? monitoringService, Func<bool> action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        return new SafeDispatcherTimer(intervalSeconds, logger, monitoringService, action);
    }

    public void Start() => _dispatcherTimer.Start();

    private void DispatcherTick(object? sender, EventArgs e)
    {
        try
        {
            _dispatcherTimer.Stop();
            bool doContinue = _action();
            if (doContinue)
                _dispatcherTimer.Start();
        }
        catch (Exception ex)
        {
            _logger?.LogException(_monitoringService, ex);
            if (_logger == null)
                MessageBox.Show("Unhandled exception: " + ex, "Reqnroll error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
