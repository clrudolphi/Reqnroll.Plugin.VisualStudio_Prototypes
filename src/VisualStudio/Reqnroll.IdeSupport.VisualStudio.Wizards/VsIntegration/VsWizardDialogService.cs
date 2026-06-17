// VsIntegration layer — VS SDK references are expected here.
using Microsoft.Internal.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.VsIntegration;

public class VsWizardDialogService : IWizardDialogService
{
    private readonly IVsUIShell _vsUiShell;
    private readonly IMonitoringService? _monitoringService;

    public VsWizardDialogService(IVsUIShell vsUiShell, IMonitoringService? monitoringService = null)
    {
        _vsUiShell = vsUiShell;
        _monitoringService = monitoringService;
    }

    public AddNewProjectWizardResult? ShowAddNewProjectDialog()
    {
        var vm = new AddNewReqnrollProjectViewModel();
        var dialog = new AddNewReqnrollProjectDialog(vm);
        WireLinkClicked(dialog);
        ApplyTheme(dialog);
        int result = WindowHelper.ShowModal(dialog);
        if (result != 1)
            return null;

        return new AddNewProjectWizardResult(
            vm.DotNetFramework,
            vm.UnitTestFramework);
    }

    public void ShowWelcomeDialog()
    {
        var vm = new WelcomeDialogViewModel();
        var dialog = new WelcomeDialog(vm);
        WireLinkClicked(dialog);
        ApplyTheme(dialog);
        WindowHelper.ShowModal(dialog);
    }

    public void ShowUpgradeDialog(string newVersion, string changeLog)
    {
        var vm = new UpgradeDialogViewModel(newVersion, changeLog);
        var dialog = new WelcomeDialog(vm);
        WireLinkClicked(dialog);
        ApplyTheme(dialog);
        WindowHelper.ShowModal(dialog);
    }

    private void WireLinkClicked(System.Windows.Window dialog)
    {
        if (_monitoringService is null) return;
        if (dialog is WizardWindow wizard)
        {
            wizard.LinkClicked += (sender, e) =>
            {
                var uri = e.Uri;
                if (uri is null) return;
                var uriString = uri.ToString();
                if (uriString.StartsWith("file"))
                    return;

                var source = dialog.DataContext?.GetType().Name
                                 ?.Replace("ViewModel", "") ?? "Unknown";
                _monitoringService.MonitorLinkClicked(source, uriString);
            };
        }
    }

    private static void ApplyTheme(System.Windows.Window dialog)
    {
        var wizardResources = dialog.Resources.MergedDictionaries
            .OfType<WizardResources>()
            .FirstOrDefault();
        wizardResources?.ApplyVsTheme(VsThemeResourceProvider.GetThemedResources());
    }
}
