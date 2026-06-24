#nullable enable

using System.IO;
using Gherkin.Ast;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// The immutable set of step binding matches for one feature document, cached against
/// <c>(DocumentId, Owner, DocumentVersion, RegistryVersion)</c>. This is the value stored by
/// <see cref="IBindingMatchService"/> and queried by Go to Definition (F5), the diagnostics
/// aggregator (F3) and find-usages (F14/F18).
/// </summary>
public sealed class FeatureBindingMatchSet
{
    public static readonly FeatureBindingMatchSet Empty =
        new(string.Empty, ProjectOwner.Unknown, null, 0, Array.Empty<StepBindingMatch>());

    public FeatureBindingMatchSet(
        string documentId,
        ProjectOwner owner,
        int? documentVersion,
        int registryVersion,
        IReadOnlyList<StepBindingMatch> steps)
    {
        Key = new MatchSetKey(
            documentId ?? throw new ArgumentNullException(nameof(documentId)),
            owner.IsKnown ? owner : ProjectOwner.Unknown);
        DocumentVersion = documentVersion;
        RegistryVersion = registryVersion;
        Steps           = steps ?? throw new ArgumentNullException(nameof(steps));
    }

    /// <summary>The composite cache key: document URI + owning project.</summary>
    public MatchSetKey Key { get; }

    /// <summary>The feature document URI (derived from <see cref="Key"/>).</summary>
    public string DocumentId => Key.DocumentId;

    /// <summary>The owning project for this match set (derived from <see cref="Key"/>).</summary>
    public ProjectOwner Owner => Key.Owner;

    /// <summary>The feature document version these matches were computed for, when known.</summary>
    public int? DocumentVersion { get; }

    /// <summary>The <see cref="ProjectBindingRegistry.Version"/> these matches were computed against.</summary>
    public int RegistryVersion { get; }

    public IReadOnlyList<StepBindingMatch> Steps { get; }

    public IEnumerable<StepBindingMatch> Undefined => Steps.Where(s => s.IsUndefined);
    public IEnumerable<StepBindingMatch> Defined   => Steps.Where(s => s.IsDefined);
    public IEnumerable<StepBindingMatch> Ambiguous => Steps.Where(s => s.IsAmbiguous);

    /// <summary>The step whose text span contains <paramref name="offset"/>, or null. Used by F5.</summary>
    public StepBindingMatch? FindAt(int offset) => Steps.FirstOrDefault(s => s.Contains(offset));

    /// <summary>
    /// Builds a match set from the flattened tag collection produced by <c>DeveroomTagParser</c>.
    /// Each <see cref="DeveroomTagTypes.DefinedStep"/> / <see cref="DeveroomTagTypes.UndefinedStep"/> /
    /// <see cref="DeveroomTagTypes.AmbiguousStep"/> tag carries the step text span as its range and the
    /// computed <see cref="MatchResult"/> as its data; this method projects those into
    /// <see cref="StepBindingMatch"/> entries.
    /// </summary>
    /// <remarks>
    /// A single step can emit both a DefinedStep and an UndefinedStep tag (e.g. a scenario outline
    /// whose example rows partly match), but both reference the same <see cref="MatchResult"/> at the
    /// same span; such duplicates are collapsed so the set holds one entry per step.
    /// </remarks>
    public static FeatureBindingMatchSet FromTags(
        string documentId,
        int? documentVersion,
        int registryVersion,
        IEnumerable<DeveroomTag> tags,
        ProjectOwner owner = default)
    {
        var byStart = new Dictionary<int, StepBindingMatch>();

        // Short project name for the Project column (e.g. "Minimal", "Minimalnet481").
        var projectName = owner.IsKnown
            ? Path.GetFileNameWithoutExtension(owner.ProjectFile)
            : null;

        foreach (var tag in tags)
        {
            if (tag.Type is not (DeveroomTagTypes.DefinedStep or DeveroomTagTypes.UndefinedStep or DeveroomTagTypes.AmbiguousStep))
                continue;
            if (tag.Data is not MatchResult match)
                continue;

            // Collapse the DefinedStep/UndefinedStep pair a single step may emit: same span, same result.
            if (!byStart.ContainsKey(tag.Range.Start))
            {
                // Walk up the tag hierarchy to extract keyword and scenario name at parse time
                // so the handler does not have to re-derive them from snapshot text later.
                //   DefinedStep.ParentTag  = StepBlock       (Data = Gherkin.Ast.Step)
                //   StepBlock.ParentTag    = ScenarioDefinitionBlock (Data = IHasDescription)
                var stepBlockTag      = tag.ParentTag;
                var scenarioDefTag    = stepBlockTag?.ParentTag;
                var keyword           = (stepBlockTag?.Data as Step)?.Keyword?.Trim();
                var scenarioName      = (scenarioDefTag?.Data as IHasDescription)?.Name;
                if (string.IsNullOrEmpty(scenarioName)) scenarioName = null;

                byStart[tag.Range.Start] = new StepBindingMatch(
                    documentId, tag.Range, match, keyword, scenarioName, projectName);
            }
        }

        var steps = byStart.Values.OrderBy(s => s.Range.Start).ToArray();
        return new FeatureBindingMatchSet(documentId, owner, documentVersion, registryVersion, steps);
    }
}
