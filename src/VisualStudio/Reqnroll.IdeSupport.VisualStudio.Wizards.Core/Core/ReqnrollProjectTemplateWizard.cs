using Reqnroll.IdeSupport.VisualStudio.Wizards.Utilities; 

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Core;

/// <summary>
/// Ported from Reqnroll.VisualStudio\Wizards\ReqnrollProjectWizard.cs.
/// Uses IWizardContext instead of WizardRunParameters. Has no dependency
/// on the UI assembly — dialog selections arrive as AddNewProjectWizardResult.
/// The VS MEF [Export] attribute is NOT present here — the VsIntegration layer
/// resolves this through VsTemplateWizardBase.ResolveWizard().
/// </summary>
public class ReqnrollProjectTemplateWizard : ITemplateWizard
{
    private readonly IWizardDialogService _dialogService;
    private readonly IWizardTelemetry _telemetry;

    public ReqnrollProjectTemplateWizard(IWizardDialogService dialogService, IWizardTelemetry telemetry)
    {
        _dialogService = dialogService;
        _telemetry = telemetry;
    }

    public bool RunStarted(IWizardContext context)
    {
        _telemetry.OnProjectTemplateWizardStarted();

        var result = _dialogService.ShowAddNewProjectDialog();
        if (result == null)
            return false;

        _telemetry.OnProjectTemplateWizardCompleted(
            result.DotNetFramework,
            result.UnitTestFramework);

        // Clean the project name to ensure it is a valid C# identifier for RootNamespace.
        var projectNameParts = context.ReplacementsDictionary["$projectname$"].Split('.');
        var proposedProjectName = string.Join(".", projectNameParts);
        var cleanedProjectName = string.Join(".",
            projectNameParts.Select(part => part.ToIdentifier()).ToArray());
        var rootNamespace = proposedProjectName != cleanedProjectName ? cleanedProjectName : string.Empty;

        context.ReplacementsDictionary.Add("$dotnetframework$", result.DotNetFramework);
        context.ReplacementsDictionary.Add(WizardContextKeys.IsNetFrameworkKey,
            result.IsNetFramework.ToString(System.Globalization.CultureInfo.InvariantCulture));
        context.ReplacementsDictionary.Add("$unittestframework$", result.UnitTestFramework);
        context.ReplacementsDictionary.Add("$rootnamespace$", rootNamespace);

        if (!result.IsNetFramework)
        {
            var globalUsings = new StringBuilder();
            switch (result.UnitTestFramework)
            {
                case "MSTest":
                    globalUsings.AppendLine("    <Using Include=\"Microsoft.VisualStudio.TestTools.UnitTesting\" />");
                    break;
                case "NUnit":
                    globalUsings.AppendLine("    <Using Include=\"NUnit.Framework\" />");
                    break;
                case "xUnit":
                case "xUnit.v3":
                    globalUsings.AppendLine("    <Using Include=\"Xunit\" />");
                    break;
                // TUnit: no global using required
            }

            context.ReplacementsDictionary.Add("$globalUsings$", globalUsings.ToString());
        }

        return true;
    }
}
