// Ported from Reqnroll.VisualStudio.UI\Dialogs\AddNewReqnrollProjectDialog.xaml.cs
// IVsUIShell dependency removed.
using System.Windows.Controls;
using Reqnroll.IdeSupport.VisualStudio.Wizards.UI.Dialogs;
using Reqnroll.IdeSupport.VisualStudio.Wizards.UI.ViewModels;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.UI.Dialogs;

public partial class AddNewReqnrollProjectDialog : WizardWindow
{
    public AddNewReqnrollProjectDialog()
    {
        InitializeComponent();
    }

    public AddNewReqnrollProjectDialog(AddNewReqnrollProjectViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public AddNewReqnrollProjectViewModel? ViewModel { get; }

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void TestFramework_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (ViewModel != null)
            ViewModel.UnitTestFramework = e.AddedItems[0]?.ToString() ?? string.Empty;
        e.Handled = true;
    }
}
