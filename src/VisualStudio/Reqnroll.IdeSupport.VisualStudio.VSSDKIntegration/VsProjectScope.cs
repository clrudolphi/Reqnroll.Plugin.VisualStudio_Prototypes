#nullable disable

using EnvDTE;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
using Reqnroll.IdeSupport.VisualStudio.Common;
using Reqnroll.IdeSupport.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.Shell;
using System.Collections.Concurrent;

namespace Reqnroll.IdeSupport.VisualStudio.SDKIntegration;

public class VsProjectScope : IProjectScope
{
    private readonly Project _project;

    public VsProjectScope(string id, Project project, IIdeScope ideScope)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _project = project;
        IdeScope = ideScope;
        ProjectFolder = VsUtils.GetProjectFolder(project);
        ProjectName = project.Name;
        ProjectFullName = project.FullName;
        Debug.Assert(ProjectFolder != null, "VsxHelper.IsSolutionProject ensures a not-null ProjectFolder");
    }

    private IDeveroomLogger Logger => IdeScope.Logger;
    private IMonitoringService MonitoringService => IdeScope.MonitoringService;
    public ConcurrentDictionary<Type, object> Properties { get; } = new();
    public string ProjectFolder { get; }
    public string OutputAssemblyPath { get { ThreadHelper.ThrowIfNotOnUIThread(); return VsUtils.GetOutputAssemblyPath(_project); } }
    public string TargetFrameworkMoniker { get { ThreadHelper.ThrowIfNotOnUIThread(); return VsUtils.GetTargetFrameworkMoniker(_project); } }
    public string TargetFrameworkMonikers { get { ThreadHelper.ThrowIfNotOnUIThread(); return VsUtils.GetTargetFrameworkMonikers(_project); } }
    public string PlatformTargetName { get { ThreadHelper.ThrowIfNotOnUIThread(); return VsUtils.GetPlatformTargetName(_project) ?? VsUtils.GetPlatformName(_project); } }
    public string ProjectName { get; }
    public string ProjectFullName { get; }
    public string DefaultNamespace { get { ThreadHelper.ThrowIfNotOnUIThread(); return GetDefaultNamespace(); } }

    public IIdeScope IdeScope { get; }
    public IEnumerable<NuGetPackageReference> PackageReferences { get { ThreadHelper.ThrowIfNotOnUIThread(); return GetPackageReferences(); } }

    //public void AddFile(string targetFilePath, string template)
    //{
    //    //TODO: handle template parameters
    //    IdeScope.FileSystem.File.WriteAllText(targetFilePath, template);
    //    _project.ProjectItems.AddFromFile(targetFilePath);
    //}

    public int? GetFeatureFileCount()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return VsUtils.GetPhysicalFileProjectItems(_project)
                .Count(pi => FileSystemHelper.IsOfType(VsUtils.GetFilePath(pi), ".feature"));
        }
        catch (Exception e)
        {
            Logger.LogVerboseException(MonitoringService, e);
            return null;
        }
    }

    public string[] GetProjectFiles(string extension)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return VsUtils.GetPhysicalFileProjectItems(_project)
                .Select(VsUtils.GetFilePath)
                .Where(fp => FileSystemHelper.IsOfType(fp, extension))
                .ToArray();
        }
        catch (Exception e)
        {
            Logger.LogVerboseException(MonitoringService, e);
            return new string[0];
        }
    }

    public void Dispose()
    {
        foreach (var disposableProperty in Properties.Values.OfType<IDisposable>())
            disposableProperty.Dispose();
    }

    private string GetDefaultNamespace()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return _project.Properties.Item("DefaultNamespace")?.Value as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private NuGetPackageReference[] GetPackageReferences()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return VsUtils.GetInstalledNuGetPackages((IdeScope as VsIdeScope).ServiceProvider, _project.FullName)
                .Select(pmd =>
                    new NuGetPackageReference(pmd.Id, new NuGetVersion(pmd.Version, pmd.RequestedRange),
                        pmd.InstallPath))
                .ToArray();
        }
        catch (Exception e)
        {
            if (IdeScope.IsSolutionLoaded)
                Logger.LogVerboseException(MonitoringService, e);
            else
                Logger.LogVerbose("Loading package references failed, solution is not loaded fully yet.");
            return null;
        }
    }

    public override string ToString() => ProjectName;

    public ProjectSettings GetProjectSettings()
    {
        throw new NotImplementedException();
    }

    public void InitializeServices()
    {
        ConfigurationProjectSystemExtensions.GetDeveroomConfigurationProvider(this);
        this.GetProjectSettingsProvider();
    }
}
