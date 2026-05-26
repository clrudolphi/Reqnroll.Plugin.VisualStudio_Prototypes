// Minimal diagnostics types needed by the wizard layer.
// In the new LSP extension these will be replaced by a project reference
// to whatever diagnostics assembly is used there.
namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

public interface IDeveroomLogger
{
    void LogVerbose(string message, [CallerMemberName] string callerName = "???");
    void LogException(IWizardTelemetryLogger? telemetry, Exception ex, string message = "Exception",
        [CallerMemberName] string callerName = "???");
    void LogDebugException(Exception ex, string message = "Exception",
        [CallerMemberName] string callerName = "???");
}

/// <summary>
/// Minimal telemetry surface needed by SafeDispatcherTimer for error reporting.
/// Kept separate from IWizardTelemetry which covers wizard-specific events.
/// </summary>
public interface IWizardTelemetryLogger
{
    void MonitorError(Exception exception, bool? isFatal = null);
}
