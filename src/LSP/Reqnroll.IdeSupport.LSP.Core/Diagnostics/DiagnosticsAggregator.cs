using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Diagnostics;

/// <inheritdoc cref="IDiagnosticsAggregator"/>
public sealed class DiagnosticsAggregator : IDiagnosticsAggregator
{
    /// <summary>LSP <c>source</c> field value for Gherkin parse errors (F4).</summary>
    public const string ParserSource  = "reqnroll.parser";

    /// <summary>LSP <c>source</c> field value for step binding mismatches (F3).</summary>
    public const string BindingSource = "reqnroll.binding";

    /// <summary>Hover message shown for every unmatched step.</summary>
    public const string UndefinedStepMessage = "Step definition not found.";

    /// <inheritdoc/>
    public IReadOnlyList<GherkinDiagnostic> Aggregate(
        IReadOnlyCollection<DeveroomTag> tags,
        FeatureBindingMatchSet matchSet)
    {
        var diagnostics = new List<GherkinDiagnostic>();

        // F4 — parse errors: each is stored as a DeveroomTag of type ParserError whose
        // Data holds the parser exception message string.
        foreach (var tag in tags)
        {
            if (tag.Type != DeveroomTagTypes.ParserError)
                continue;

            var message = tag.Data as string ?? "Gherkin parse error.";
            diagnostics.Add(new GherkinDiagnostic(
                message,
                tag.Range,
                GherkinDiagnosticSeverity.Error,
                ParserSource));
        }

        // F3 — binding mismatches: undefined steps from the binding match set.
        // Ambiguous steps are intentionally excluded (aspiration, not yet in scope —
        // see Non-Goals in §2 of the design doc).
        foreach (var step in matchSet.Undefined)
        {
            diagnostics.Add(new GherkinDiagnostic(
                UndefinedStepMessage,
                step.Range,
                GherkinDiagnosticSeverity.Warning,
                BindingSource));
        }

        return diagnostics;
    }
}
