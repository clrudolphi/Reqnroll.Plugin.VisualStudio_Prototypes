namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

/// <summary>
/// Data returned by IWizardDialogService.ShowAddNewProjectDialog().
/// Lives in Core/Abstractions so wizard logic can read selections
/// without any dependency on WPF ViewModels or the UI assembly.
/// </summary>
public sealed class AddNewProjectWizardResult
{
    public AddNewProjectWizardResult(
        string dotNetFramework,
        string unitTestFramework)
    {
        DotNetFramework = dotNetFramework;
        UnitTestFramework = unitTestFramework;
    }

    public string DotNetFramework { get; }
    public string UnitTestFramework { get; }

    public bool IsNetFramework =>
        DotNetFramework.StartsWith("net4", StringComparison.OrdinalIgnoreCase);
}
