namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

/// <summary>
/// Replaces IDeveroomWizard. Implemented by FeatureFileTemplateWizard,
/// ReqnrollProjectTemplateWizard, and ConfigFileTemplateWizard.
/// </summary>
public interface ITemplateWizard
{
    bool RunStarted(IWizardContext context);
}
