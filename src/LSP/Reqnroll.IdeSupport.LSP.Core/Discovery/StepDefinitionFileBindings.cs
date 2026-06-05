namespace Reqnroll.IdeSupport.LSP.Core.Discovery;

/// <summary>
/// The complete set of bindings discovered in a single C# source file by Roslyn-based
/// (source-level) discovery: step definitions and hooks.
/// </summary>
public record StepDefinitionFileBindings(
    IReadOnlyList<ProjectStepDefinitionBinding> StepDefinitions,
    IReadOnlyList<ProjectHookBinding> Hooks)
{
    public static readonly StepDefinitionFileBindings Empty =
        new(Array.Empty<ProjectStepDefinitionBinding>(), Array.Empty<ProjectHookBinding>());
}
