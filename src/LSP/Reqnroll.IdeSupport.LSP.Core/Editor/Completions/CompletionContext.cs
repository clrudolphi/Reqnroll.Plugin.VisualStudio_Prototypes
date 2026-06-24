using Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

namespace Reqnroll.IdeSupport.LSP.Core.Editor.Completions;

/// <summary>
/// Base type for the two mutually exclusive completion contexts resolved from a cursor position
/// in a .feature file. Use a <c>switch</c> expression on the concrete subtype to dispatch.
/// </summary>
public abstract class CompletionContext { }

/// <summary>
/// Cursor is on a keyword position (start of line, or before any step text).
/// <see cref="ExpectedTokens"/> is empty when the document could not be parsed — callers should
/// fall back to <see cref="ICompletionService.GetDefaultKeywordCompletions"/> in that case.
/// </summary>
public sealed class KeywordCompletionContext : CompletionContext
{
    public GherkinDialect Dialect        { get; }
    public TokenType[]    ExpectedTokens { get; }

    public KeywordCompletionContext(GherkinDialect dialect, TokenType[] expectedTokens)
    {
        Dialect        = dialect;
        ExpectedTokens = expectedTokens;
    }
}

/// <summary>
/// Cursor is on a step line, past the step keyword — trigger F8 step-definition sample completion.
/// </summary>
public sealed class StepCompletionContext : CompletionContext
{
    /// <summary>The Gherkin step the cursor is on, used to filter completions by <c>ScenarioBlock</c>.</summary>
    public DeveroomGherkinStep Step               { get; }

    /// <summary>Text the user has typed after the keyword and its trailing space.</summary>
    public string              TypedAfterKeyword  { get; }

    /// <summary>Zero-based column index of the first character after the keyword (the start of the step text).</summary>
    public int                 StepTextStartColumn { get; }

    public StepCompletionContext(
        DeveroomGherkinStep step,
        string              typedAfterKeyword,
        int                 stepTextStartColumn)
    {
        Step                = step;
        TypedAfterKeyword   = typedAfterKeyword;
        StepTextStartColumn = stepTextStartColumn;
    }
}
