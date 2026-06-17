#nullable disable
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Analytics;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;

namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// No-op <see cref="IMonitoringService"/> for the LSP server process.
/// Telemetry is not collected from the server side.
/// </summary>
public sealed class NullMonitoringService : IMonitoringService
{
    public static readonly NullMonitoringService Instance = new();

    private NullMonitoringService() { }

    public void MonitorLoadProjectSystem() { }
    public void MonitorOpenProjectSystem(IIdeScope ideScope) { }
    public void MonitorOpenProject(ProjectSettings settings, int? featureFileCount) { }
    public void MonitorOpenFeatureFile(ProjectSettings projectSettings) { }
    public void MonitorExtensionInstalled() { }
    public void MonitorExtensionUpgraded(string oldExtensionVersion) { }
    public void MonitorExtensionDaysOfUsage(int usageDays) { }
    public void MonitorCommandAddFeatureFile(ProjectSettings projectSettings) { }
    public void MonitorCommandAddReqnrollConfigFile(ProjectSettings projectSettings) { }
    public void MonitorError(System.Exception exception, bool? isFatal = null) { }
    public void MonitorProjectTemplateWizardStarted() { }
    public void MonitorProjectTemplateWizardCompleted(string dotNetFramework, string unitTestFramework, bool addFluentAssertions) { }
    public void MonitorUpgradeDialogDismissed(Dictionary<string, object> additionalProps) { }
    public void MonitorWelcomeDialogDismissed(Dictionary<string, object> additionalProps) { }
    public void MonitorLinkClicked(string source, string url, Dictionary<string, object> additionalProps = null) { }
    public void TransmitEvent(IAnalyticsEvent runtimeEvent) { }
}
