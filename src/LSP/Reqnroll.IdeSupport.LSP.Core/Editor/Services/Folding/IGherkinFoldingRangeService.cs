using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

namespace Reqnroll.IdeSupport.LSP.Core.Editor.Services.Folding;

/// <summary>
/// Computes foldable regions (F10) from the DeveroomTag tree.
/// </summary>
public interface IGherkinFoldingRangeService
{
    /// <summary>
    /// Returns a list of folding ranges for the given feature-file tag tree.
    /// Returns an empty list when no feature is present.
    /// </summary>
    IReadOnlyList<GherkinFoldingRange> BuildFoldingRanges(IReadOnlyCollection<DeveroomTag> tags);
}
