namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Core;

/// <summary>
/// Ported from Reqnroll.VisualStudio\Wizards\ReqnrollConfigFileWizard.cs.
/// </summary>
public class ConfigFileTemplateWizard : ITemplateWizard
{
    public bool RunStarted(IWizardContext context)
    {
        var settings = context.ProjectSettings;

        context.Telemetry.OnConfigFileAdded(settings);

        if (settings.IsSpecFlowProject)
        {
            // Pre-3.6.23 SpecFlow config files need to be copied to output
            context.ReplacementsDictionary[WizardContextKeys.CopyToOutputDirectoryKey] = "PreserveNewest";
        }

        return true;
    }
}
