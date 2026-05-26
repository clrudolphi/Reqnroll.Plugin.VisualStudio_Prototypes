namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Core;

/// <summary>
/// Ported from Reqnroll.VisualStudio\Wizards\FeatureFileWizard.cs.
/// All VS SDK dependency removed — operates purely through IWizardContext.
/// </summary>
public class FeatureFileTemplateWizard : ITemplateWizard
{
    // Kept as a constant so the VsIntegration layer can reference it
    // when building replacement dictionaries without knowing this class.
    public const string CustomToolReqnrollSingleFileGenerator = "ReqnrollSingleFileGenerator";
    public const string BuildActionReqnrollEmbeddedFeature = "ReqnrollEmbeddedFeature";
    public const string ReqnrollToolsMsBuildGenerationPackageName = "Reqnroll.Tools.MsBuild.Generation";

    public bool RunStarted(IWizardContext context)
    {
        var settings = context.ProjectSettings;

        context.Telemetry.OnFeatureFileAdded(settings);

        if (settings.IsReqnrollProject)
        {
            if (settings.DesignTimeFeatureFileGenerationEnabled)
            {
                context.ReplacementsDictionary[WizardContextKeys.CustomToolSettingKey] =
                    CustomToolReqnrollSingleFileGenerator;
            }
            else if (!settings.HasDesignTimeGenerationReplacement)
            {
                context.ShowProblem(
                    $"In order to be able to run the Reqnroll scenarios as tests, you need to install the " +
                    $"'{ReqnrollToolsMsBuildGenerationPackageName}' NuGet package to the project.");
            }

            if (settings.HasXUnitAdapter)
            {
                context.ReplacementsDictionary[WizardContextKeys.BuildActionKey] =
                    BuildActionReqnrollEmbeddedFeature;
            }
        }

        return true;
    }
}
