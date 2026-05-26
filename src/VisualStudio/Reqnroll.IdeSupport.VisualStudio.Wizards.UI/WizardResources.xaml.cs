// Code-behind for WizardResources.xaml.
// When running inside VS the VsIntegration layer calls ApplyVsTheme()
// to overwrite the static brush/style values with live VS theme colours.
// Outside VS (standalone WPF host, tests) the XAML static values are used as-is.
namespace Reqnroll.IdeSupport.VisualStudio.Wizards.UI;

public partial class WizardResources : ResourceDictionary
{
    public WizardResources()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Called by VsWizardDialogService after the VS theme is available.
    /// Accepts a dictionary of key→resource pairs produced by the VS integration layer.
    /// </summary>
    public void ApplyVsTheme(IReadOnlyDictionary<string, object> themedResources)
    {
        foreach (var kvp in themedResources)
            this[kvp.Key] = kvp.Value;
    }
}
