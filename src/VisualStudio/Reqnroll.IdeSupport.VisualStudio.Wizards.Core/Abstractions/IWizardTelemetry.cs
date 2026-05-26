namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

/// <summary>
/// Telemetry surface for wizard events only — extracted from the
/// full IMonitoringService. Implemented by VsWizardTelemetry in the
/// VsIntegration layer which delegates to the real IMonitoringService.
/// </summary>
public interface IWizardTelemetry
{
    void OnFeatureFileAdded(WizardProjectSettings projectSettings);
    void OnConfigFileAdded(WizardProjectSettings projectSettings);
    void OnProjectTemplateWizardStarted();
    void OnProjectTemplateWizardCompleted(string dotNetFramework, string unitTestFramework);
}
