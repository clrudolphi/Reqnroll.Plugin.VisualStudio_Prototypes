using Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Editor.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

namespace Reqnroll.IdeSupport.LSP.Core.Editor.Completions;

public interface ICompletionService
{
    /// <summary>
    /// Returns keyword completions for the given set of expected Gherkin token types.
    /// Called when the Gherkin parser can determine valid tokens at the cursor position.
    /// </summary>
    CompletionResult GetKeywordCompletions(TokenType[] expectedTokens, GherkinDialect dialect);

    /// <summary>
    /// Returns the full set of keyword completions regardless of parse context.
    /// Used as a fallback when the document cannot be parsed or yields no expected tokens.
    /// </summary>
    CompletionResult GetDefaultKeywordCompletions(GherkinDialect dialect);

    /// <summary>
    /// Returns step-definition samples that match the given <paramref name="step"/>'s
    /// <see cref="ScenarioBlock"/> type, ranked by <paramref name="matcher"/>.
    /// </summary>
    CompletionResult GetStepCompletions(
        DeveroomGherkinStep                    step,
        string                                 typedAfterKeyword,
        ProjectBindingRegistry                 registry,
        Func<ProjectStepDefinitionBinding, int> usageCounter,
        ICompletionMatcher                     matcher);
}
