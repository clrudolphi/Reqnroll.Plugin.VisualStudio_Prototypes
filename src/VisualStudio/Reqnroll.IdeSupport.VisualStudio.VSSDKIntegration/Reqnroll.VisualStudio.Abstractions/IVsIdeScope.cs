using EnvDTE;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.VisualStudio.Package.ProjectSystem;

/// <summary>
/// Extends IIdeScope with the VS-SDK-specific ability to resolve a DTE Project
/// into an IProjectScope. Lives in VSSDKIntegration (not Wizards.Core) because
/// EnvDTE.Project is a VS SDK type.
/// </summary>
public interface IVsIdeScope : IIdeScope
{
    IProjectScope GetProjectScope(Project project);
}