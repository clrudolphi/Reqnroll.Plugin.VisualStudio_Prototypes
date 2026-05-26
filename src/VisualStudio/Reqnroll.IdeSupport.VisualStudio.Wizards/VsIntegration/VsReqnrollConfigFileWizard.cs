// VsIntegration layer

using Reqnroll.IdeSupport.VisualStudio.Wizards.Core;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.VsIntegration;

/// <summary>
/// Ported from VsReqnrollConfigFileWizard.
/// </summary>
public class VsReqnrollConfigFileWizard : VsTemplateWizardBase<ConfigFileTemplateWizard>
{
    protected override ConfigFileTemplateWizard ResolveWizard(EnvDTE.DTE dte) => new();
}
