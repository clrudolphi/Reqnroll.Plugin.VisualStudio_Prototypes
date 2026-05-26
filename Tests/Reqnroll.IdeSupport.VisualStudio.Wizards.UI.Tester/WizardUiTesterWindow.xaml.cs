using Reqnroll.IdeSupport.VisualStudio.Wizards.UI.Dialogs;
using Reqnroll.IdeSupport.VisualStudio.Wizards.UI.ViewModels;
using System.Windows;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.UI.Tester;

public partial class WizardUiTesterWindow : Window
{
    public WizardUiTesterWindow()
    {
        InitializeComponent();
    }

    private void ShowWelcomeDialog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WelcomeDialog(new WelcomeDialogViewModel());
        dialog.ShowDialog();
    }

    private void ShowUpgradeDialog_Click(object sender, RoutedEventArgs e)
    {
        const string sampleChangelog = """
            # v2.1.0 - 2025-06-01

            ## New Features

            * Improved step definition matching
            * Faster project analysis

            ## Bug Fixes

            * Fixed null-reference in feature file parser (#42)
            """;

        var dialog = new WelcomeDialog(new UpgradeDialogViewModel("2.1.0", sampleChangelog));
        dialog.ShowDialog();
    }

    private void ShowAddNewProjectDialog_Click(object sender, RoutedEventArgs e)
    {
        var vm = new AddNewReqnrollProjectViewModel();
        var dialog = new AddNewReqnrollProjectDialog(vm);
        if (dialog.ShowDialog() == true)
            MessageBox.Show(
                $"Framework: {vm.DotNetFramework}\nTest runner: {vm.UnitTestFramework}",
                "Selected options");
    }
}
