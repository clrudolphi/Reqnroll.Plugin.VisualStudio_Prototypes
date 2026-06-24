using Gherkin;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Document;
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

namespace Reqnroll.IdeSupport.LSP.Core.Editor.Completions;

/// <summary>
/// Walks the parsed Deveroom tag tree for a snapshot to decide whether the cursor calls for
/// keyword completion (F7) or step-definition sample completion (F8), and packages all the
/// Gherkin-specific data the handler needs to fulfil the request.
/// </summary>
public sealed class CompletionContextResolver : ICompletionContextResolver
{
    private readonly IDeveroomTagParser _tagParser;
    private readonly IMonitoringService _monitoringService;

    public CompletionContextResolver(IDeveroomTagParser tagParser, IMonitoringService monitoringService)
    {
        _tagParser         = tagParser;
        _monitoringService = monitoringService;
    }

    public CompletionContext? Resolve(
        IGherkinTextSnapshot   snapshot,
        int                    cursorLine,
        int                    cursorChar,
        ProjectBindingRegistry registry,
        string                 fallbackLanguage)
    {
        var tags = _tagParser.Parse(snapshot, registry);

        var docTag     = tags.FirstOrDefault(t => t.Type == DeveroomTagTypes.Document);
        var gherkinDoc = docTag?.Data as DeveroomGherkinDocument;
        var dialect    = gherkinDoc?.GherkinDialect
                      ?? new GherkinDialectProvider(fallbackLanguage).DefaultDialect;

        // ── F8: step completion ─────────────────────────────────────────────────
        // Cursor must be on a recognised step line and at or past the step text start
        // (i.e. after the keyword and its trailing space).
        var stepTag = tags.FirstOrDefault(t =>
            t.Type == DeveroomTagTypes.StepBlock &&
            t.Data is DeveroomGherkinStep s &&
            s.Location.Line - 1 == cursorLine);   // Gherkin lines are 1-based

        if (stepTag?.Data is DeveroomGherkinStep cursorStep)
        {
            var snapshotLine = snapshot.GetLineFromLineNumber(cursorLine);
            var cursorOffset = snapshotLine.Start + cursorChar;

            // Gherkin Location.Column is 1-based; Keyword includes the trailing space.
            var stepTextStart = snapshotLine.Start
                              + (cursorStep.Location.Column - 1)
                              + cursorStep.Keyword.Length;

            if (cursorOffset >= stepTextStart)
            {
                var typed = snapshot.GetText()
                    .Substring(stepTextStart, cursorOffset - stepTextStart);
                var stepTextStartColumn = stepTextStart - snapshotLine.Start;

                return new StepCompletionContext(cursorStep, typed, stepTextStartColumn);
            }
        }

        // ── F7: keyword completion ──────────────────────────────────────────────
        var tokens = gherkinDoc?.GetExpectedTokens(cursorLine, _monitoringService)
                     ?? Array.Empty<TokenType>();

        return new KeywordCompletionContext(dialect, tokens);
    }
}
