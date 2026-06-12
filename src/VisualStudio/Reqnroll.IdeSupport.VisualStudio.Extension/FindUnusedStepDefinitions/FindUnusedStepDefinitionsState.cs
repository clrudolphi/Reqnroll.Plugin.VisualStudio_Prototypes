#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// Shared, container-registered holder for the runtime-created F15 components.
/// Follows the same pattern as <see cref="FindStepUsages.FindStepUsagesState"/>.
/// </summary>
internal sealed class FindUnusedStepDefinitionsState
{
    /// <summary>Set once the server has initialised; null before that and after dispose.</summary>
    public FindUnusedStepDefinitionsService? Service { get; set; }

    /// <summary>Set once the server has initialised; null before that and after dispose.</summary>
    public FindUnusedStepDefinitionsRenderer? Renderer { get; set; }
}
