using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;





namespace Reqnroll.IdeSupport.LSP.Core.Completions;

/// <summary>
/// Determines what kind of completion to offer at a cursor position in a .feature file,
/// encapsulating all Gherkin structural knowledge (tag walking, step offset arithmetic,
/// dialect resolution, and F7/F8 dispatch).
/// </summary>
public interface ICompletionContextResolver
{
    /// <summary>
    /// Resolves the completion context for the given cursor position.
    /// Returns <see langword="null"/> only when no completion is appropriate
    /// (e.g. the buffer is empty or the cursor is inside a comment).
    /// Otherwise returns a <see cref="KeywordCompletionContext"/> or
    /// <see cref="StepCompletionContext"/>.
    /// </summary>
    /// <param name="snapshot">Current document snapshot.</param>
    /// <param name="cursorLine">0-based line index of the cursor.</param>
    /// <param name="cursorChar">0-based character index of the cursor within the line.</param>
    /// <param name="registry">Binding registry for the owning project; may be <see cref="ProjectBindingRegistry.Invalid"/>.</param>
    /// <param name="fallbackLanguage">
    /// BCP-47 language tag (e.g. <c>"en"</c>, <c>"de"</c>) used to resolve the Gherkin dialect
    /// when the document cannot be parsed. Callers derive this from project configuration;
    /// Core uses it only as a last resort.
    /// </param>
    CompletionContext? Resolve(
        IGherkinTextSnapshot   snapshot,
        int                    cursorLine,
        int                    cursorChar,
        ProjectBindingRegistry registry,
        string                 fallbackLanguage);
}
