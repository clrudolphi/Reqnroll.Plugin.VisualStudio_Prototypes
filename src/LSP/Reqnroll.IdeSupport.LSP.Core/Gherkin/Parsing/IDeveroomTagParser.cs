using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

public interface IDeveroomTagParser
{
    /// <summary>
    /// Parse <paramref name="fileSnapshot"/> and return Deveroom tags annotated with
    /// binding matches from <paramref name="bindingRegistry"/>.
    /// Pass <see cref="ProjectBindingRegistry.Invalid"/> when no registry is available yet;
    /// step-matching tags will simply be omitted.
    /// </summary>
    IReadOnlyCollection<DeveroomTag> Parse(
        IGherkinTextSnapshot fileSnapshot,
        ProjectBindingRegistry bindingRegistry);
}
