namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

/// <summary>
/// Shows wizard-owned dialogs. Implemented by VsWizardDialogService
/// in the VsIntegration layer (using IVsUIShell) and by a plain WPF
/// implementation for use outside of VS (e.g. the UI tester harness).
///
/// Named methods instead of ShowDialog&lt;TViewModel&gt; so that Core has
/// no dependency on WPF ViewModel types in the UI assembly. Each method
/// returns a plain DTO (or null when the user cancelled) so Core wizard
/// logic remains fully testable without WPF hosting.
/// </summary>
public interface IWizardDialogService
{
    /// <summary>
    /// Shows the Add New Reqnroll Project dialog.
    /// Returns the user's selections, or null if cancelled.
    /// </summary>
    AddNewProjectWizardResult? ShowAddNewProjectDialog();

    /// <summary>
    /// Shows the Welcome dialog (first-install flow).
    /// </summary>
    void ShowWelcomeDialog();

    /// <summary>
    /// Shows the Upgrade/changelog dialog.
    /// </summary>
    //void ShowUpgradeDialog(string newVersion, string changeLog);
}
