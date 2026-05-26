namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

/// <summary>
/// Replaces WizardRunParameters. Exposes only what wizard logic
/// actually needs — no IProjectScope, no IIdeScope, no editor services.
/// </summary>
public interface IWizardContext
{
    bool IsAddNewItem { get; }
    string TemplateFolder { get; }
    string TargetFolder { get; }
    string TargetFileName { get; set; }
    Dictionary<string, string> ReplacementsDictionary { get; }

    /// <summary>Computed project settings — not the full IProjectScope.</summary>
    WizardProjectSettings ProjectSettings { get; }

    IWizardDialogService DialogService { get; }
    IWizardTelemetry Telemetry { get; }

    void ShowProblem(string message);
}
