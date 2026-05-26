// Ported from Reqnroll.VisualStudio.UI\Dialogs\WelcomeDialog.xaml.cs
// IVsUIShell dependency removed — WizardWindow base handles hosting.
using Reqnroll.IdeSupport.VisualStudio.Wizards.UI.Dialogs;
using Reqnroll.IdeSupport.VisualStudio.Wizards.UI.ViewModels.WizardDialogs;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.UI.Dialogs;

public partial class WelcomeDialog : WizardWindow
{
    public WelcomeDialog()
    {
        InitializeComponent();
    }

    public WelcomeDialog(WizardViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public WizardViewModel? ViewModel { get; }
}
