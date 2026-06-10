#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

/// <summary>
/// A single navigation destination returned by a server-side query.
/// Carries both the display string for the picker UI and the coordinates needed to open the file.
/// </summary>
/// <remarks>
/// Shared by Go to Hooks (F17) and future Go to Step Definition multi-binding picker (F5 ambiguous).
/// </remarks>
internal sealed record NavigationTarget(
    /// <summary>Label shown in the picker dialog (e.g. "[BeforeScenario] SetUpDatabase (Hooks.cs:10)").</summary>
    string DisplayText,
    /// <summary>Absolute file-system path of the target source file.</summary>
    string FilePath,
    /// <summary>0-based line in the target file.</summary>
    int StartLine,
    /// <summary>0-based character in the target file.</summary>
    int StartChar);
