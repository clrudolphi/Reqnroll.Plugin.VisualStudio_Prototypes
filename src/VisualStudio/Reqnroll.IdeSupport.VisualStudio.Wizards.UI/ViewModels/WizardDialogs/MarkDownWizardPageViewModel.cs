// Ported from Reqnroll.VisualStudio\UI\ViewModels\WizardDialogs\MarkDownWizardPageViewModel.cs
namespace Reqnroll.IdeSupport.VisualStudio.Wizards.UI.ViewModels.WizardDialogs;

public class MarkDownWizardPageViewModel : WizardPageViewModel
{
    public MarkDownWizardPageViewModel(string name) : base(name)
    {
    }

    public string Text { get; set; } = string.Empty;
}
