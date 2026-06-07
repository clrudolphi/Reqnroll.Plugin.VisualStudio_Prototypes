#nullable enable

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// The composite cache key for <see cref="IBindingMatchService"/>: a feature document URI plus
/// the project whose registry was used to compute the match set.
/// A shared/linked feature may have one match set per owning project (Q18 phase 2B).
/// </summary>
public readonly record struct MatchSetKey(string DocumentId, ProjectOwner Owner)
{
    /// <summary>Builds a key with <see cref="ProjectOwner.Unknown"/> for the pre-baseline case.</summary>
    public static MatchSetKey ForUnknownProject(string documentId) =>
        new(documentId, ProjectOwner.Unknown);
}
