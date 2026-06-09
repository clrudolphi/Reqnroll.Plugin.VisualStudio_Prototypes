#nullable enable

using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Document;

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// A single feature-file step's resolved binding match, together with the text span the
/// step occupies in the feature document. This is the per-<c>(featureURI, range)</c>
/// coordinate of the binding match cache described in section 3 of the
/// LSP IDE Support design.
/// </summary>
/// <remarks>
/// Match <em>computation</em> still happens in <c>DeveroomTagParser</c> while it walks the
/// document (it has the snapshot for span math and the tag tree for
/// <see cref="IGherkinDocumentContext"/>). A <see cref="StepBindingMatch"/> captures the
/// result of that computation so downstream features — Go to Definition (F5), diagnostics
/// (F3), find usages (F14) — can query it without re-parsing.
/// </remarks>
public sealed class StepBindingMatch
{
    public StepBindingMatch(
        string       featureDocumentId,
        GherkinRange range,
        MatchResult  result,
        string?      keyword      = null,
        string?      scenarioName = null,
        string?      projectName  = null)
    {
        FeatureDocumentId = featureDocumentId ?? throw new ArgumentNullException(nameof(featureDocumentId));
        Range      = range  ?? throw new ArgumentNullException(nameof(range));
        Result     = result ?? throw new ArgumentNullException(nameof(result));
        Keyword      = keyword;
        ScenarioName = scenarioName;
        ProjectName  = projectName;
    }

    /// <summary>
    /// The document ID (URI string) of the feature file that contains this step.
    /// Backs Find Usages (F14) and Code Lens usage counts (F18): callers need the feature file
    /// URI to build <c>Location</c> responses without a separate document-ID lookup.
    /// </summary>
    public string FeatureDocumentId { get; }

    /// <summary>The span of the step text (excluding the keyword) within the feature document.</summary>
    public GherkinRange Range { get; }

    /// <summary>The full match result for the step (Defined / Undefined / Ambiguous, plus errors).</summary>
    public MatchResult Result { get; }

    public bool IsUndefined => Result.HasUndefined;
    public bool IsDefined => Result.HasDefined;
    public bool IsAmbiguous => Result.HasAmbiguous;

    /// <summary>True when <paramref name="offset"/> (absolute char offset) falls within the step text span.</summary>
    public bool Contains(int offset) => offset >= Range.Start && offset < Range.End;

    /// <summary>
    /// The Gherkin step keyword as it appears in the feature file, trimmed
    /// (e.g. <c>"Given"</c>, <c>"When"</c>, <c>"Then"</c>, <c>"And"</c>).
    /// <see langword="null"/> when the match was built without AST context.
    /// </summary>
    public string? Keyword { get; }

    /// <summary>
    /// The name of the scenario or scenario outline that contains this step
    /// (e.g. <c>"Add two numbers"</c>).
    /// <see langword="null"/> for Background steps or when AST context was unavailable.
    /// </summary>
    public string? ScenarioName { get; }

    /// <summary>
    /// The short project name derived from <c>ProjectOwner.ProjectFile</c> at cache-build time
    /// (e.g. <c>"Minimal"</c>, <c>"Minimalnet481"</c>).
    /// <see langword="null"/> when the owner was unknown at cache-build time.
    /// </summary>
    public string? ProjectName { get; }

    /// <summary>
    /// The source locations of every binding this step resolves to — one for a unique match,
    /// several for an ambiguous match, none for an undefined step.
    /// </summary>
    public IEnumerable<SourceLocation> BindingLocations =>
        Result.Items
            .Where(i => i.MatchedStepDefinition?.Implementation?.SourceLocation != null)
            .Select(i => i.MatchedStepDefinition.Implementation.SourceLocation!);
}
