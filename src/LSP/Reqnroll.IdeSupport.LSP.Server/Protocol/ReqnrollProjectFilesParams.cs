using MediatR;
namespace Reqnroll.IdeSupport.LSP.Server.Protocol;

/// <summary>
/// Payload for the <c>reqnroll/projectFiles</c> client-to-server notification.
/// Sent by IDE glue to establish or update the authoritative project-membership index so
/// the server knows which files each project claims (including linked files, excluded files,
/// and glob-resolved SDK-style includes).
/// </summary>
public sealed class ReqnrollProjectFilesParams : INotification
{
    /// <summary>Absolute path of the <c>.csproj</c> file.  Part 1 of the index key.</summary>
    public string ProjectFile { get; set; } = string.Empty;

    /// <summary>
    /// Full target-framework moniker, e.g. <c>.NETCoreApp,Version=v8.0</c>.
    /// Part 2 of the index key; distinguishes per-TFM conditional membership for
    /// multi-targeted projects (Phase 1 ignores TFM; reserved for a follow-up).
    /// </summary>
    public string TargetFrameworkMoniker { get; set; } = string.Empty;

    /// <summary>
    /// Whether this payload is an initial complete snapshot (<see cref="ProjectFilesKind.Baseline"/>)
    /// or an incremental update (<see cref="ProjectFilesKind.Delta"/>).
    /// </summary>
    public ProjectFilesKind Kind { get; set; } = ProjectFilesKind.Baseline;

    /// <summary>
    /// For a baseline: all files owned by this (project, TFM) combination.
    /// For a delta: the changed entries only (use <see cref="ProjectFileEntry.Added"/> to distinguish).
    /// </summary>
    public ProjectFileEntry[] Files { get; set; } = [];
}

/// <summary>Distinguishes a full snapshot from an incremental update.</summary>
public enum ProjectFilesKind
{
    Baseline = 0,
    Delta    = 1,
}

/// <summary>One file attributed to a project in a <see cref="ReqnrollProjectFilesParams"/> payload.</summary>
public sealed class ProjectFileEntry
{
    /// <summary>Absolute on-disk path with link targets already resolved.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Whether this file contributes feature scenarios (<see cref="ProjectFileRole.Feature"/>)
    /// or step-definition bindings (<see cref="ProjectFileRole.Binding"/>).
    /// </summary>
    public ProjectFileRole Role { get; set; }

    /// <summary>
    /// For <see cref="ProjectFilesKind.Delta"/> payloads only:
    /// <see langword="true"/> = add to index, <see langword="false"/> = remove from index.
    /// Ignored on baselines.
    /// </summary>
    public bool Added { get; set; } = true;
}

/// <summary>Classifies a project file's contribution to the Reqnroll model.</summary>
public enum ProjectFileRole
{
    Feature = 0,
    Binding = 1,
}
