#nullable enable

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// Identifies the project that owns a feature-file binding match set.
/// Used as the project dimension of <see cref="MatchSetKey"/>.
/// </summary>
public readonly record struct ProjectOwner(string ProjectFile, string Tfm)
{
    /// <summary>Sentinel used when the owning project is not yet known (e.g. no baseline received).</summary>
    public static readonly ProjectOwner Unknown = new(string.Empty, string.Empty);

    public bool IsKnown => !string.IsNullOrEmpty(ProjectFile);
}
